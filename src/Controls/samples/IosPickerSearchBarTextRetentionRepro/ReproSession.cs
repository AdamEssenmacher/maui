using System.Runtime.CompilerServices;
using System.Text;
using CoreGraphics;
using Foundation;
using Microsoft.Maui.Handlers;
using Microsoft.Maui.Platform;
using UIKit;

namespace IosPickerSearchBarTextRetentionRepro;

internal static class ReproSession
{
	const int CyclesPerControlType = 48;
	const int PayloadKiBPerControl = 512;
	const int ControlTypeCount = 2;
	const long PayloadBytesPerControl = PayloadKiBPerControl * 1024L;

	public static ReproReport Run(IMauiContext mauiContext)
	{
		ForceFullGc();
		var baselineBytes = GC.GetTotalMemory(true);

		var control = RunScenario(mauiContext, clearNativeText: true);
		var leak = RunScenario(mauiContext, clearNativeText: false);

		ForceFullGc();
		var finalBytes = GC.GetTotalMemory(true);

		return new ReproReport(
			CyclesPerControlType,
			PayloadKiBPerControl,
			baselineBytes,
			finalBytes,
			control,
			leak);
	}

	static ScenarioResult RunScenario(IMauiContext mauiContext, bool clearNativeText)
	{
		var retainedNativeControls = new List<object>(CyclesPerControlType * ControlTypeCount);
		var tracked = new List<TrackedControl>(CyclesPerControlType * ControlTypeCount);

		for (var i = 0; i < CyclesPerControlType; i++)
		{
			CreatePickerCycle(mauiContext, retainedNativeControls, tracked, i, clearNativeText);
			CreateSearchBarCycle(mauiContext, retainedNativeControls, tracked, i, clearNativeText);
		}

		ForceFullGc();

		var name = clearNativeText
			? "control: retained Picker/SearchBar native peers after explicit native text clear"
			: "current handlers: retained Picker/SearchBar native peers after handler disconnect";
		var result = ScenarioResult.From(name, tracked);

		GC.KeepAlive(retainedNativeControls);
		return result;
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	static void CreatePickerCycle(
		IMauiContext mauiContext,
		List<object> retainedNativeControls,
		List<TrackedControl> tracked,
		int cycle,
		bool clearNativeText)
	{
		var itemText = CreateOperationalText("picker item", cycle);
		var picker = new Picker
		{
			Title = "Select retained offline work item",
			CharacterSpacing = 0.1
		};
		picker.Items.Add(itemText);
		picker.SelectedIndex = 0;

		var handler = (PickerHandler)picker.ToHandler(mauiContext);
		var platformView = handler.PlatformView;
		platformView.Frame = new CGRect(0, 0, 720, 48);
		handler.UpdateValue(nameof(IPicker.Items));
		handler.UpdateValue(nameof(IPicker.SelectedIndex));
		handler.UpdateValue(nameof(IPicker.Title));
		handler.UpdateValue(nameof(IPicker.CharacterSpacing));

		tracked.Add(TrackedControl.Create("Picker", picker, handler, platformView, PayloadBytesPerControl));

		((IElementHandler)handler).DisconnectHandler();
		picker.Items.Clear();
		picker.SelectedIndex = -1;
		picker.Title = null;
		picker.BindingContext = null;

		if (clearNativeText)
			ClearNativeText(platformView);

		retainedNativeControls.Add(platformView);
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	static void CreateSearchBarCycle(
		IMauiContext mauiContext,
		List<object> retainedNativeControls,
		List<TrackedControl> tracked,
		int cycle,
		bool clearNativeText)
	{
		var queryText = CreateOperationalText("search query", cycle);
		var searchBar = new SearchBar
		{
			Text = queryText,
			Placeholder = "Paste a generated support query, audit filter, or copied log excerpt",
			CharacterSpacing = 0.1
		};

		var handler = (SearchBarHandler)searchBar.ToHandler(mauiContext);
		var platformView = handler.PlatformView;
		platformView.Frame = new CGRect(0, 0, 720, 48);
		handler.UpdateValue(nameof(ISearchBar.Text));
		handler.UpdateValue(nameof(ISearchBar.Placeholder));
		handler.UpdateValue(nameof(ISearchBar.CharacterSpacing));

		tracked.Add(TrackedControl.Create("SearchBar", searchBar, handler, platformView, PayloadBytesPerControl));

		((IElementHandler)handler).DisconnectHandler();
		searchBar.Text = null;
		searchBar.Placeholder = null;
		searchBar.BindingContext = null;

		if (clearNativeText)
			ClearNativeText(platformView);

		retainedNativeControls.Add(platformView);
	}

	static string CreateOperationalText(string controlType, int cycle)
	{
		var header = $"Cycle {cycle:000} retained {controlType} text. ";
		var sentence = "This copied support record includes generated filters, account notes, trace excerpts, and offline lookup text. ";
		var targetChars = (int)(PayloadBytesPerControl / 2);
		var builder = new StringBuilder(targetChars + sentence.Length);
		builder.Append(header);

		while (builder.Length < targetChars)
			builder.Append(sentence);

		if (builder.Length > targetChars)
			builder.Length = targetChars;

		return builder.ToString();
	}

	static void ClearNativeText(MauiPicker picker)
	{
		picker.Text = null;
		picker.AttributedText = null;
		picker.Placeholder = null;
		picker.AttributedPlaceholder = null;
		picker.InputView = null;
		picker.InputAccessoryView = null;
		picker.UIPickerView = null;
	}

	static void ClearNativeText(MauiSearchBar searchBar)
	{
		searchBar.Text = null;
		searchBar.Placeholder = null;
	}

	static long EstimateNativeTextBytes(object nativeControl)
	{
		return nativeControl switch
		{
			MauiPicker picker => EstimateTextBytes(picker.Text, picker.AttributedText?.Value) +
				EstimateTextBytes(picker.Placeholder, picker.AttributedPlaceholder?.Value),
			MauiSearchBar searchBar => EstimateSearchBarBytes(searchBar),
			_ => 0
		};
	}

	static long EstimateSearchBarBytes(MauiSearchBar searchBar)
	{
		return EstimateTextBytes(searchBar.Text, null) +
			EstimateTextBytes(searchBar.Placeholder, null);
	}

	static long EstimateTextBytes(string? text, string? attributedText)
	{
		var retainedText = attributedText ?? text;
		return string.IsNullOrEmpty(retainedText) ? 0 : retainedText.Length * 2L;
	}

	static void ForceFullGc()
	{
		for (var i = 0; i < 4; i++)
		{
			GC.Collect();
			GC.WaitForPendingFinalizers();
			GC.Collect();
		}
	}

	internal sealed record TrackedControl(
		string Kind,
		WeakReference VirtualView,
		WeakReference Handler,
		WeakReference NativeControl,
		long ExpectedTextBytes)
	{
		public static TrackedControl Create(string kind, IView virtualView, IElementHandler handler, object nativeControl, long expectedTextBytes)
		{
			return new TrackedControl(
				kind,
				new WeakReference(virtualView),
				new WeakReference(handler),
				new WeakReference(nativeControl),
				expectedTextBytes);
		}
	}

	internal sealed record KindSummary(
		string Kind,
		int Total,
		int AliveVirtualViews,
		int AliveHandlers,
		int AliveNativeControls,
		int NativeControlsWithText,
		long EstimatedNativeTextBytes);

	internal sealed record ScenarioResult(
		string Name,
		int TrackedControls,
		int AliveVirtualViews,
		int AliveHandlers,
		int AliveNativeControls,
		int NativeControlsWithText,
		long EstimatedNativeTextBytes,
		IReadOnlyList<KindSummary> ByKind)
	{
		public static ScenarioResult From(string name, IReadOnlyList<TrackedControl> controls)
		{
			var summaries = controls
				.GroupBy(control => control.Kind)
				.Select(group => Summarize(group.Key, group))
				.OrderBy(summary => summary.Kind, StringComparer.Ordinal)
				.ToArray();

			return new ScenarioResult(
				name,
				controls.Count,
				summaries.Sum(summary => summary.AliveVirtualViews),
				summaries.Sum(summary => summary.AliveHandlers),
				summaries.Sum(summary => summary.AliveNativeControls),
				summaries.Sum(summary => summary.NativeControlsWithText),
				summaries.Sum(summary => summary.EstimatedNativeTextBytes),
				summaries);
		}

		static KindSummary Summarize(string kind, IEnumerable<TrackedControl> controls)
		{
			var total = 0;
			var aliveVirtualViews = 0;
			var aliveHandlers = 0;
			var aliveNativeControls = 0;
			var nativeControlsWithText = 0;
			long estimatedNativeTextBytes = 0;

			foreach (var control in controls)
			{
				total++;

				if (control.VirtualView.IsAlive)
					aliveVirtualViews++;

				if (control.Handler.IsAlive)
					aliveHandlers++;

				if (control.NativeControl.Target is { } nativeControl)
				{
					aliveNativeControls++;
					var bytes = EstimateNativeTextBytes(nativeControl);
					if (bytes > 0)
					{
						nativeControlsWithText++;
						estimatedNativeTextBytes += Math.Min(bytes, control.ExpectedTextBytes);
					}
				}
			}

			return new KindSummary(
				kind,
				total,
				aliveVirtualViews,
				aliveHandlers,
				aliveNativeControls,
				nativeControlsWithText,
				estimatedNativeTextBytes);
		}
	}

	internal sealed record ReproReport(
		int CyclesPerControlType,
		int PayloadKiBPerControl,
		long BaselineManagedBytes,
		long FinalManagedBytes,
		ScenarioResult Control,
		ScenarioResult Leak)
	{
		public bool LeakProved =>
			Control.NativeControlsWithText == 0 &&
			Control.AliveVirtualViews <= Control.ByKind.Count &&
			Control.AliveHandlers <= Control.ByKind.Count &&
			Leak.NativeControlsWithText == Leak.TrackedControls &&
			Leak.EstimatedNativeTextBytes >= Leak.TrackedControls * PayloadKiBPerControl * 1024L * 0.95 &&
			Leak.AliveVirtualViews <= Leak.ByKind.Count &&
			Leak.AliveHandlers <= Leak.ByKind.Count;

		public string ToText()
		{
			return string.Join(Environment.NewLine,
				"iOS Picker/SearchBar native text slot retention repro",
				$"Cycles per control type: {CyclesPerControlType}",
				$"Control types: Picker, SearchBar",
				$"Payload per native text control: {PayloadKiBPerControl} KiB",
				$"Leak proved: {LeakProved}",
				string.Empty,
				FormatScenario(Control),
				string.Empty,
				FormatScenario(Leak),
				string.Empty,
				$"Managed heap baseline: {FormatBytes(BaselineManagedBytes)}",
				$"Managed heap final: {FormatBytes(FinalManagedBytes)}",
				$"Managed heap delta: {FormatBytes(FinalManagedBytes - BaselineManagedBytes)}");
		}

		static string FormatScenario(ScenarioResult result)
		{
			var lines = new List<string>
			{
				$"Scenario: {result.Name}",
				$"  Tracked native text controls: {result.TrackedControls}",
				$"  Virtual views alive: {result.AliveVirtualViews}/{result.TrackedControls}",
				$"  Handlers alive: {result.AliveHandlers}/{result.TrackedControls}",
				$"  Native controls alive: {result.AliveNativeControls}/{result.TrackedControls}",
				$"  Native controls still carrying text: {result.NativeControlsWithText}/{result.TrackedControls}",
				$"  Estimated retained native text bytes: {FormatBytes(result.EstimatedNativeTextBytes)}"
			};

			foreach (var summary in result.ByKind)
			{
				lines.Add(
					$"  {summary.Kind}: text={summary.NativeControlsWithText}/{summary.Total}, native={summary.AliveNativeControls}/{summary.Total}, views={summary.AliveVirtualViews}/{summary.Total}, handlers={summary.AliveHandlers}/{summary.Total}, bytes={FormatBytes(summary.EstimatedNativeTextBytes)}");
			}

			return string.Join(Environment.NewLine, lines);
		}

		static string FormatBytes(long bytes)
		{
			var sign = bytes < 0 ? "-" : string.Empty;
			var value = Math.Abs(bytes);

			if (value >= 1024L * 1024L * 1024L)
				return $"{sign}{value / 1024d / 1024d / 1024d:0.0} GiB";

			if (value >= 1024L * 1024L)
				return $"{sign}{value / 1024d / 1024d:0.0} MiB";

			if (value >= 1024L)
				return $"{sign}{value / 1024d:0.0} KiB";

			return $"{sign}{value} B";
		}
	}
}
