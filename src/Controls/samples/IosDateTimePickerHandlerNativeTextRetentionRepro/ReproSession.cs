using System.Runtime.CompilerServices;
using System.Text;
using Foundation;
using Microsoft.Maui.Handlers;
using Microsoft.Maui.Platform;
using UIKit;

namespace IosDateTimePickerHandlerNativeTextRetentionRepro;

internal static class ReproSession
{
	const int CyclesPerPickerType = 128;
	const int PayloadKiBPerPicker = 8;
	const int PickerTypeCount = 2;
	const long PayloadBytesPerPicker = PayloadKiBPerPicker * 1024L;

	public static ReproReport Run(IMauiContext mauiContext)
	{
		ForceFullGc();
		var baselineBytes = GC.GetTotalMemory(true);

		var control = RunScenario(mauiContext, clearNativeText: true);
		var leak = RunScenario(mauiContext, clearNativeText: false);

		ForceFullGc();
		var finalBytes = GC.GetTotalMemory(true);

		return new ReproReport(
			CyclesPerPickerType,
			PayloadKiBPerPicker,
			baselineBytes,
			finalBytes,
			control,
			leak);
	}

	static ScenarioResult RunScenario(IMauiContext mauiContext, bool clearNativeText)
	{
		var retainedNativePickers = new List<object>(CyclesPerPickerType * PickerTypeCount);
		var tracked = new List<TrackedPicker>(CyclesPerPickerType * PickerTypeCount);

		for (var i = 0; i < CyclesPerPickerType; i++)
		{
			CreateDatePickerCycle(mauiContext, retainedNativePickers, tracked, i, clearNativeText);
			CreateTimePickerCycle(mauiContext, retainedNativePickers, tracked, i, clearNativeText);
		}

		ForceFullGc();

		var name = clearNativeText
			? "control: retained native iOS date/time picker text fields after explicit native Text clear"
			: "current handlers: retained native iOS date/time picker text fields after handler disconnect";
		var result = ScenarioResult.From(name, tracked);

		GC.KeepAlive(retainedNativePickers);
		return result;
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	static void CreateDatePickerCycle(
		IMauiContext mauiContext,
		List<object> retainedNativePickers,
		List<TrackedPicker> tracked,
		int cycle,
		bool clearNativeText)
	{
		var format = CreateDateFormat(cycle);
		var datePicker = new DatePicker
		{
			Date = new DateTime(2026, 7, 1).AddDays(cycle % 28),
			Format = format,
			CharacterSpacing = 0.1
		};

		var handler = (DatePickerHandler)datePicker.ToHandler(mauiContext);
		var platformView = handler.PlatformView;
		handler.UpdateValue(nameof(IDatePicker.Date));
		handler.UpdateValue(nameof(IDatePicker.Format));
		handler.UpdateValue(nameof(IDatePicker.CharacterSpacing));

		tracked.Add(TrackedPicker.Create("DatePicker", datePicker, handler, platformView, PayloadBytesPerPicker));

		((IElementHandler)handler).DisconnectHandler();
		datePicker.Format = "d";
		datePicker.BindingContext = null;

		if (clearNativeText)
			ClearNativeText(platformView);

		retainedNativePickers.Add(platformView);
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	static void CreateTimePickerCycle(
		IMauiContext mauiContext,
		List<object> retainedNativePickers,
		List<TrackedPicker> tracked,
		int cycle,
		bool clearNativeText)
	{
		var format = CreateTimeFormat(cycle);
		var timePicker = new TimePicker
		{
			Time = new TimeSpan(8 + (cycle % 10), cycle % 60, 0),
			Format = format,
			CharacterSpacing = 0.1
		};

		var handler = (TimePickerHandler)timePicker.ToHandler(mauiContext);
		var platformView = handler.PlatformView;
		handler.UpdateValue(nameof(ITimePicker.Time));
		handler.UpdateValue(nameof(ITimePicker.Format));
		handler.UpdateValue(nameof(ITimePicker.CharacterSpacing));

		tracked.Add(TrackedPicker.Create("TimePicker", timePicker, handler, platformView, PayloadBytesPerPicker));

		((IElementHandler)handler).DisconnectHandler();
		timePicker.Format = "t";
		timePicker.BindingContext = null;

		if (clearNativeText)
			ClearNativeText(platformView);

		retainedNativePickers.Add(platformView);
	}

	static string CreateDateFormat(int cycle)
	{
		return "yyyy-MM-dd " + CreateLiteralPayload("route schedule date", cycle);
	}

	static string CreateTimeFormat(int cycle)
	{
		return "HH:mm " + CreateLiteralPayload("dispatch window time", cycle);
	}

	static string CreateLiteralPayload(string label, int cycle)
	{
		var targetChars = (int)(PayloadBytesPerPicker / 2);
		var sentence = $"{label} cycle {cycle:0000} imported calendar payload with SLA notes and localized shift metadata. ";
		var builder = new StringBuilder(targetChars + 32);
		builder.Append('\'');

		while (builder.Length < targetChars)
			builder.Append(sentence);

		if (builder.Length > targetChars)
			builder.Length = targetChars;

		builder.Append('\'');
		return builder.ToString();
	}

	static void ClearNativeText(UITextField textField)
	{
		textField.Text = null;
		textField.AttributedText = null;
	}

	static long EstimateNativeTextBytes(UITextField textField)
	{
		var retainedText = textField.AttributedText?.Value ?? textField.Text;
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

	internal sealed record TrackedPicker(
		string Kind,
		WeakReference VirtualView,
		WeakReference Handler,
		WeakReference NativePicker,
		long ExpectedTextBytes)
	{
		public static TrackedPicker Create(string kind, IView virtualView, IElementHandler handler, UITextField nativePicker, long expectedTextBytes)
		{
			return new TrackedPicker(
				kind,
				new WeakReference(virtualView),
				new WeakReference(handler),
				new WeakReference(nativePicker),
				expectedTextBytes);
		}
	}

	internal sealed record KindSummary(
		string Kind,
		int Total,
		int AliveVirtualViews,
		int AliveHandlers,
		int AliveNativePickers,
		int NativePickersWithText,
		long EstimatedNativeTextBytes);

	internal sealed record ScenarioResult(
		string Name,
		int TrackedPickers,
		int AliveVirtualViews,
		int AliveHandlers,
		int AliveNativePickers,
		int NativePickersWithText,
		long EstimatedNativeTextBytes,
		IReadOnlyList<KindSummary> ByKind)
	{
		public static ScenarioResult From(string name, IReadOnlyList<TrackedPicker> pickers)
		{
			var summaries = pickers
				.GroupBy(picker => picker.Kind)
				.Select(group => Summarize(group.Key, group))
				.OrderBy(summary => summary.Kind, StringComparer.Ordinal)
				.ToArray();

			return new ScenarioResult(
				name,
				pickers.Count,
				summaries.Sum(summary => summary.AliveVirtualViews),
				summaries.Sum(summary => summary.AliveHandlers),
				summaries.Sum(summary => summary.AliveNativePickers),
				summaries.Sum(summary => summary.NativePickersWithText),
				summaries.Sum(summary => summary.EstimatedNativeTextBytes),
				summaries);
		}

		static KindSummary Summarize(string kind, IEnumerable<TrackedPicker> pickers)
		{
			var total = 0;
			var aliveVirtualViews = 0;
			var aliveHandlers = 0;
			var aliveNativePickers = 0;
			var nativePickersWithText = 0;
			long estimatedNativeTextBytes = 0;

			foreach (var picker in pickers)
			{
				total++;

				if (picker.VirtualView.IsAlive)
					aliveVirtualViews++;

				if (picker.Handler.IsAlive)
					aliveHandlers++;

				if (picker.NativePicker.Target is UITextField nativePicker)
				{
					aliveNativePickers++;
					var bytes = EstimateNativeTextBytes(nativePicker);
					if (bytes > 0)
					{
						nativePickersWithText++;
						estimatedNativeTextBytes += Math.Min(bytes, picker.ExpectedTextBytes);
					}
				}
			}

			return new KindSummary(
				kind,
				total,
				aliveVirtualViews,
				aliveHandlers,
				aliveNativePickers,
				nativePickersWithText,
				estimatedNativeTextBytes);
		}
	}

	internal sealed record ReproReport(
		int CyclesPerPickerType,
		int PayloadKiBPerPicker,
		long BaselineManagedBytes,
		long FinalManagedBytes,
		ScenarioResult Control,
		ScenarioResult Leak)
	{
		public bool LeakProved =>
			Control.NativePickersWithText == 0 &&
			Control.AliveVirtualViews <= Control.ByKind.Count &&
			Control.AliveHandlers <= Control.ByKind.Count &&
			Leak.NativePickersWithText == Leak.TrackedPickers &&
			Leak.EstimatedNativeTextBytes >= Leak.TrackedPickers * PayloadKiBPerPicker * 1024L * 0.95 &&
			Leak.AliveVirtualViews <= Leak.ByKind.Count &&
			Leak.AliveHandlers <= Leak.ByKind.Count;

		public string ToText()
		{
			return string.Join(Environment.NewLine,
				"RESULT: " + (LeakProved ? "PROVEN" : "NOT PROVEN"),
				"iOS DatePicker/TimePicker native text retention repro",
				$"Cycles per picker type: {CyclesPerPickerType}",
				$"Picker types: DatePicker, TimePicker",
				$"Payload per native picker text field: {PayloadKiBPerPicker} KiB",
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
				$"  Tracked native picker text fields: {result.TrackedPickers}",
				$"  Virtual views alive: {result.AliveVirtualViews}/{result.TrackedPickers}",
				$"  Handlers alive: {result.AliveHandlers}/{result.TrackedPickers}",
				$"  Native pickers alive: {result.AliveNativePickers}/{result.TrackedPickers}",
				$"  Native pickers still carrying text: {result.NativePickersWithText}/{result.TrackedPickers}",
				$"  Estimated retained native text bytes: {FormatBytes(result.EstimatedNativeTextBytes)}"
			};

			foreach (var summary in result.ByKind)
			{
				lines.Add(
					$"  {summary.Kind}: text={summary.NativePickersWithText}/{summary.Total}, native={summary.AliveNativePickers}/{summary.Total}, views={summary.AliveVirtualViews}/{summary.Total}, handlers={summary.AliveHandlers}/{summary.Total}, bytes={FormatBytes(summary.EstimatedNativeTextBytes)}");
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
