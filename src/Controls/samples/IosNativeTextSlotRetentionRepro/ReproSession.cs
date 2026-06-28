using System.Runtime.CompilerServices;
using System.Text;
using CoreGraphics;
using Microsoft.Maui.Handlers;
using Microsoft.Maui.Platform;
using UIKit;

namespace IosNativeTextSlotRetentionRepro;

internal static class ReproSession
{
	const int CyclesPerControlType = 32;
	const int PayloadKiBPerControl = 512;
	const int ControlTypeCount = 3;
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
			CreateLabelCycle(mauiContext, retainedNativeControls, tracked, i, clearNativeText);
			CreateEntryCycle(mauiContext, retainedNativeControls, tracked, i, clearNativeText);
			CreateEditorCycle(mauiContext, retainedNativeControls, tracked, i, clearNativeText);
		}

		ForceFullGc();

		var name = clearNativeText
			? "control: retained native text controls after explicit native Text/AttributedText clear"
			: "current handlers: retained native text controls after handler disconnect";
		var result = ScenarioResult.From(name, tracked);

		GC.KeepAlive(retainedNativeControls);
		return result;
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	static void CreateLabelCycle(
		IMauiContext mauiContext,
		List<object> retainedNativeControls,
		List<TrackedControl> tracked,
		int cycle,
		bool clearNativeText)
	{
		var text = CreateDocumentText("label", cycle);
		var label = new Label
		{
			Text = text,
			LineBreakMode = LineBreakMode.WordWrap,
			CharacterSpacing = 0.1
		};

		var handler = (LabelHandler)label.ToHandler(mauiContext);
		var platformView = handler.PlatformView;
		platformView.Frame = new CGRect(0, 0, 720, 160);
		handler.UpdateValue(nameof(ILabel.Text));
		handler.UpdateValue(nameof(ILabel.CharacterSpacing));

		tracked.Add(TrackedControl.Create("Label", label, handler, platformView, PayloadBytesPerControl));

		((IElementHandler)handler).DisconnectHandler();
		label.Text = null;
		label.BindingContext = null;

		if (clearNativeText)
			ClearNativeText(platformView);

		retainedNativeControls.Add(platformView);
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	static void CreateEntryCycle(
		IMauiContext mauiContext,
		List<object> retainedNativeControls,
		List<TrackedControl> tracked,
		int cycle,
		bool clearNativeText)
	{
		var text = CreateDocumentText("entry", cycle);
		var entry = new Entry
		{
			Text = text,
			Placeholder = "Customer-facing notes search field",
			CharacterSpacing = 0.1
		};

		var handler = (EntryHandler)entry.ToHandler(mauiContext);
		var platformView = handler.PlatformView;
		platformView.Frame = new CGRect(0, 0, 720, 48);
		handler.UpdateValue(nameof(IEntry.Text));
		handler.UpdateValue(nameof(IEntry.Placeholder));
		handler.UpdateValue(nameof(IEntry.CharacterSpacing));

		tracked.Add(TrackedControl.Create("Entry", entry, handler, platformView, PayloadBytesPerControl));

		((IElementHandler)handler).DisconnectHandler();
		entry.Text = null;
		entry.Placeholder = null;
		entry.BindingContext = null;

		if (clearNativeText)
			ClearNativeText(platformView);

		retainedNativeControls.Add(platformView);
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	static void CreateEditorCycle(
		IMauiContext mauiContext,
		List<object> retainedNativeControls,
		List<TrackedControl> tracked,
		int cycle,
		bool clearNativeText)
	{
		var text = CreateDocumentText("editor", cycle);
		var editor = new Editor
		{
			Text = text,
			Placeholder = "Offline draft, audit note, or generated report body",
			CharacterSpacing = 0.1
		};

		var handler = (EditorHandler)editor.ToHandler(mauiContext);
		var platformView = handler.PlatformView;
		platformView.Frame = new CGRect(0, 0, 720, 240);
		handler.UpdateValue(nameof(IEditor.Text));
		handler.UpdateValue(nameof(IEditor.Placeholder));
		handler.UpdateValue(nameof(IEditor.CharacterSpacing));

		tracked.Add(TrackedControl.Create("Editor", editor, handler, platformView, PayloadBytesPerControl));

		((IElementHandler)handler).DisconnectHandler();
		editor.Text = null;
		editor.Placeholder = null;
		editor.BindingContext = null;

		if (clearNativeText)
			ClearNativeText(platformView);

		retainedNativeControls.Add(platformView);
	}

	static string CreateDocumentText(string controlType, int cycle)
	{
		var header = $"Cycle {cycle:000} {controlType} retained customer note. ";
		var sentence = "This offline record contains copied claim notes, generated summaries, and searchable audit text. ";
		var targetChars = (int)(PayloadBytesPerControl / 2);
		var builder = new StringBuilder(targetChars + sentence.Length);
		builder.Append(header);

		while (builder.Length < targetChars)
			builder.Append(sentence);

		if (builder.Length > targetChars)
			builder.Length = targetChars;

		return builder.ToString();
	}

	static void ClearNativeText(MauiLabel label)
	{
		label.Text = null;
		label.AttributedText = null;
	}

	static void ClearNativeText(MauiTextField textField)
	{
		textField.Text = null;
		textField.AttributedText = null;
		textField.AttributedPlaceholder = null;
	}

	static void ClearNativeText(MauiTextView textView)
	{
		textView.Text = null;
		textView.AttributedText = new Foundation.NSAttributedString(string.Empty);
		textView.PlaceholderText = null;
		textView.AttributedPlaceholderText = null;
	}

	static long EstimateNativeTextBytes(object nativeControl)
	{
		return nativeControl switch
		{
			MauiLabel label => EstimateTextBytes(label.Text, label.AttributedText?.Value),
			MauiTextField textField => EstimateTextBytes(textField.Text, textField.AttributedText?.Value) +
				EstimateTextBytes(null, textField.AttributedPlaceholder?.Value),
			MauiTextView textView => EstimateTextBytes(textView.Text, textView.AttributedText?.Value) +
				EstimateTextBytes(textView.PlaceholderText, textView.AttributedPlaceholderText?.Value),
			_ => 0
		};
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
				"iOS native text slot retention repro",
				$"Cycles per control type: {CyclesPerControlType}",
				$"Control types: Label, Entry, Editor",
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
