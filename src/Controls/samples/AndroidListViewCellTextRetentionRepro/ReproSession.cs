#nullable enable

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Android.Widget;
using Microsoft.Maui;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Controls.Handlers.Compatibility;

namespace AndroidListViewCellTextRetentionRepro;

internal static class ReproSession
{
	internal const int Cycles = 96;
	const int PayloadCharsPerString = 128 * 1024;
	const int BytesPerChar = 2;
	const int PayloadBytesPerString = PayloadCharsPerString * BytesPerChar;
	const int TextSlotsPerCycle = 2;
	const int TextViewSlotsPerCycle = 2;

	static readonly BindingFlags InstanceNonPublic = BindingFlags.Instance | BindingFlags.NonPublic;
	static readonly FieldInfo CellField = typeof(BaseCellView).GetField("_cell", InstanceNonPublic)
		?? throw new MissingFieldException(typeof(BaseCellView).FullName, "_cell");
	static readonly FieldInfo MainTextField = typeof(BaseCellView).GetField("_mainTextText", InstanceNonPublic)
		?? throw new MissingFieldException(typeof(BaseCellView).FullName, "_mainTextText");
	static readonly FieldInfo DetailTextField = typeof(BaseCellView).GetField("_detailTextText", InstanceNonPublic)
		?? throw new MissingFieldException(typeof(BaseCellView).FullName, "_detailTextText");
	static readonly FieldInfo MainTextViewField = typeof(BaseCellView).GetField("_mainText", InstanceNonPublic)
		?? throw new MissingFieldException(typeof(BaseCellView).FullName, "_mainText");
	static readonly FieldInfo DetailTextViewField = typeof(BaseCellView).GetField("_detailText", InstanceNonPublic)
		?? throw new MissingFieldException(typeof(BaseCellView).FullName, "_detailText");

	static readonly List<BaseCellView> RetainedNativeRows = new();

	public static async Task<ReproReport> RunAsync(IMauiContext context)
	{
		RetainedNativeRows.Clear();
		ForceFullGc();
		var baselineBytes = GC.GetTotalMemory(true);

		var control = await RunScenarioAsync(
			"control: clear BaseCellView text fields and child TextView text after disconnect",
			context,
			clearNativeTextState: true);

		var current = await RunScenarioAsync(
			"current: MAUI disconnect leaves BaseCellView text fields and child TextView text assigned",
			context,
			clearNativeTextState: false);

		ForceFullGc();
		var finalBytes = GC.GetTotalMemory(true);

		GC.KeepAlive(RetainedNativeRows);

		return new ReproReport(
			Cycles,
			PayloadCharsPerString,
			PayloadBytesPerString,
			TextSlotsPerCycle,
			TextViewSlotsPerCycle,
			baselineBytes,
			finalBytes,
			control,
			current);
	}

	static async Task<ScenarioResult> RunScenarioAsync(
		string name,
		IMauiContext context,
		bool clearNativeTextState)
	{
		var tracked = new List<TrackedCycle>(Cycles);

		for (var i = 0; i < Cycles; i++)
		{
			CreateCycle(context, i, tracked, clearNativeTextState);

			if (i % 16 == 0)
				await Task.Yield();
		}

		ForceFullGc();

		return ScenarioResult.From(name, tracked);
	}

	static void CreateCycle(
		IMauiContext context,
		int cycle,
		List<TrackedCycle> tracked,
		bool clearNativeTextState)
	{
		var mainText = CreatePayloadText("main", cycle);
		var detailText = CreatePayloadText("detail", cycle);
		var cell = new TextCell
		{
			Text = mainText,
			Detail = detailText,
			BindingContext = new object()
		};

		var renderer = new TextCellRenderer();
		renderer.ParentView = new Grid { FlowDirection = FlowDirection.LeftToRight };
		renderer.SetMauiContext(context);
		cell.Handler = renderer;
		renderer.SetVirtualView(cell);

		if (renderer.PlatformView is not BaseCellView nativeRow)
			throw new InvalidOperationException($"Expected {nameof(BaseCellView)}, got {renderer.PlatformView?.GetType().FullName ?? "<null>"}.");

		if (!IsPayloadText(GetMainTextField(nativeRow)) || !IsPayloadText(GetDetailTextField(nativeRow)))
			throw new InvalidOperationException("The TextCell renderer did not assign payload text to BaseCellView fields.");

		((IElementHandler)renderer).DisconnectHandler();

		cell.Text = null;
		cell.Detail = null;
		cell.BindingContext = null;
		ClearKnownCellBackReference(nativeRow);

		if (clearNativeTextState)
			ClearNativeTextState(nativeRow);

		RetainedNativeRows.Add(nativeRow);
		tracked.Add(TrackedCycle.Create(cycle, nativeRow, cell, renderer, mainText, detailText));
	}

	static string CreatePayloadText(string kind, int cycle)
	{
		var prefix = $"{kind}-{cycle:D4}-";
		return prefix + new string((char)('A' + (cycle % 26)), PayloadCharsPerString - prefix.Length);
	}

	static void ClearKnownCellBackReference(BaseCellView nativeRow)
	{
		CellField.SetValue(nativeRow, null);
	}

	static void ClearNativeTextState(BaseCellView nativeRow)
	{
		MainTextField.SetValue(nativeRow, null);
		DetailTextField.SetValue(nativeRow, null);

		if (MainTextViewField.GetValue(nativeRow) is TextView mainText)
			mainText.Text = string.Empty;

		if (DetailTextViewField.GetValue(nativeRow) is TextView detailText)
			detailText.Text = string.Empty;
	}

	static string? GetMainTextField(BaseCellView nativeRow)
	{
		return MainTextField.GetValue(nativeRow) as string;
	}

	static string? GetDetailTextField(BaseCellView nativeRow)
	{
		return DetailTextField.GetValue(nativeRow) as string;
	}

	static string? GetMainTextViewText(BaseCellView nativeRow)
	{
		return MainTextViewField.GetValue(nativeRow) is TextView mainText ? mainText.Text : null;
	}

	static string? GetDetailTextViewText(BaseCellView nativeRow)
	{
		return DetailTextViewField.GetValue(nativeRow) is TextView detailText ? detailText.Text : null;
	}

	static bool IsPayloadText(string? text)
	{
		return text?.Length == PayloadCharsPerString &&
			(text.StartsWith("main-", StringComparison.Ordinal) || text.StartsWith("detail-", StringComparison.Ordinal));
	}

	static long EstimateTextBytes(string? text)
	{
		return IsPayloadText(text) ? text!.Length * BytesPerChar : 0;
	}

	static void ForceFullGc()
	{
		for (var i = 0; i < 4; i++)
		{
			GC.Collect();
			GC.WaitForPendingFinalizers();
			GC.Collect();
			Java.Lang.JavaSystem.Gc();
			Thread.Sleep(100);
		}
	}

	internal sealed record TrackedCycle(
		int Cycle,
		WeakReference<BaseCellView> NativeRow,
		WeakReference<TextCell> Cell,
		WeakReference<TextCellRenderer> Renderer,
		WeakReference<string> MainText,
		WeakReference<string> DetailText)
	{
		public static TrackedCycle Create(
			int cycle,
			BaseCellView nativeRow,
			TextCell cell,
			TextCellRenderer renderer,
			string mainText,
			string detailText)
		{
			return new TrackedCycle(
				cycle,
				new WeakReference<BaseCellView>(nativeRow),
				new WeakReference<TextCell>(cell),
				new WeakReference<TextCellRenderer>(renderer),
				new WeakReference<string>(mainText),
				new WeakReference<string>(detailText));
		}
	}

	internal sealed record ScenarioResult(
		string Name,
		int TrackedCycles,
		int AliveNativeRows,
		int AliveCells,
		int AliveRenderers,
		int AliveMainTextStrings,
		int AliveDetailTextStrings,
		int NativeRowsWithPayloadMainTextField,
		int NativeRowsWithPayloadDetailTextField,
		int NativeRowsWithPayloadMainTextViewText,
		int NativeRowsWithPayloadDetailTextViewText,
		long RetainedManagedFieldTextBytes,
		long RetainedNativeTextViewBytes)
	{
		internal static ScenarioResult From(string name, IReadOnlyList<TrackedCycle> tracked)
		{
			var aliveNativeRows = 0;
			var aliveCells = 0;
			var aliveRenderers = 0;
			var aliveMainTextStrings = 0;
			var aliveDetailTextStrings = 0;
			var nativeRowsWithPayloadMainTextField = 0;
			var nativeRowsWithPayloadDetailTextField = 0;
			var nativeRowsWithPayloadMainTextViewText = 0;
			var nativeRowsWithPayloadDetailTextViewText = 0;
			long retainedManagedFieldTextBytes = 0;
			long retainedNativeTextViewBytes = 0;

			foreach (var cycle in tracked)
			{
				if (cycle.NativeRow.TryGetTarget(out var nativeRow))
				{
					aliveNativeRows++;

					var mainTextField = GetMainTextField(nativeRow);
					var detailTextField = GetDetailTextField(nativeRow);
					var mainTextViewText = GetMainTextViewText(nativeRow);
					var detailTextViewText = GetDetailTextViewText(nativeRow);

					if (IsPayloadText(mainTextField))
						nativeRowsWithPayloadMainTextField++;

					if (IsPayloadText(detailTextField))
						nativeRowsWithPayloadDetailTextField++;

					if (IsPayloadText(mainTextViewText))
						nativeRowsWithPayloadMainTextViewText++;

					if (IsPayloadText(detailTextViewText))
						nativeRowsWithPayloadDetailTextViewText++;

					retainedManagedFieldTextBytes += EstimateTextBytes(mainTextField);
					retainedManagedFieldTextBytes += EstimateTextBytes(detailTextField);
					retainedNativeTextViewBytes += EstimateTextBytes(mainTextViewText);
					retainedNativeTextViewBytes += EstimateTextBytes(detailTextViewText);
				}

				if (cycle.Cell.TryGetTarget(out _))
					aliveCells++;

				if (cycle.Renderer.TryGetTarget(out _))
					aliveRenderers++;

				if (cycle.MainText.TryGetTarget(out _))
					aliveMainTextStrings++;

				if (cycle.DetailText.TryGetTarget(out _))
					aliveDetailTextStrings++;
			}

			return new ScenarioResult(
				name,
				tracked.Count,
				aliveNativeRows,
				aliveCells,
				aliveRenderers,
				aliveMainTextStrings,
				aliveDetailTextStrings,
				nativeRowsWithPayloadMainTextField,
				nativeRowsWithPayloadDetailTextField,
				nativeRowsWithPayloadMainTextViewText,
				nativeRowsWithPayloadDetailTextViewText,
				retainedManagedFieldTextBytes,
				retainedNativeTextViewBytes);
		}
	}
}

internal sealed record ReproReport(
	int Cycles,
	int PayloadCharsPerString,
	int PayloadBytesPerString,
	int TextSlotsPerCycle,
	int TextViewSlotsPerCycle,
	long BaselineManagedBytes,
	long FinalManagedBytes,
	ReproSession.ScenarioResult Control,
	ReproSession.ScenarioResult Current)
{
	public bool LeakProved =>
		Control.AliveNativeRows == Cycles &&
		Current.AliveNativeRows == Cycles &&
		Control.NativeRowsWithPayloadMainTextField == 0 &&
		Control.NativeRowsWithPayloadDetailTextField == 0 &&
		Control.NativeRowsWithPayloadMainTextViewText == 0 &&
		Control.NativeRowsWithPayloadDetailTextViewText == 0 &&
		Control.AliveMainTextStrings == 0 &&
		Control.AliveDetailTextStrings == 0 &&
		Current.NativeRowsWithPayloadMainTextField == Cycles &&
		Current.NativeRowsWithPayloadDetailTextField == Cycles &&
		Current.NativeRowsWithPayloadMainTextViewText == Cycles &&
		Current.NativeRowsWithPayloadDetailTextViewText == Cycles &&
		Current.AliveMainTextStrings == Cycles &&
		Current.AliveDetailTextStrings == Cycles &&
		Current.AliveCells == 0 &&
		Current.AliveRenderers == 0;

	public string ToText()
	{
		var heapDeltaMiB = (FinalManagedBytes - BaselineManagedBytes) / 1024d / 1024d;
		var currentTotalBytes = Current.RetainedManagedFieldTextBytes + Current.RetainedNativeTextViewBytes;

		return string.Join(Environment.NewLine,
			"AndroidListViewCellTextRetentionRepro",
			$"Cycles: {Cycles}",
			$"Payload chars per string: {PayloadCharsPerString:N0}",
			$"Estimated bytes per string: {PayloadBytesPerString:N0}",
			$"BaseCellView field text slots per cycle: {TextSlotsPerCycle}",
			$"Child TextView text slots per cycle: {TextViewSlotsPerCycle}",
			$"Baseline managed heap: {BaselineManagedBytes:N0} bytes",
			$"Final managed heap: {FinalManagedBytes:N0} bytes",
			$"Managed heap delta: {heapDeltaMiB:N1} MiB",
			$"Leak proved: {LeakProved}",
			string.Empty,
			Format(Control),
			string.Empty,
			Format(Current),
			string.Empty,
			$"Control retained text-state payload: {FormatBytes(Control.RetainedManagedFieldTextBytes + Control.RetainedNativeTextViewBytes)}",
			$"Current retained text-state payload: {FormatBytes(currentTotalBytes)}",
			$"Current retained BaseCellView field payload: {FormatBytes(Current.RetainedManagedFieldTextBytes)}",
			$"Current estimated retained child TextView text payload: {FormatBytes(Current.RetainedNativeTextViewBytes)}",
			$"RESULT: {(LeakProved ? "PROVEN" : "NOT PROVEN")}");
	}

	static string Format(ReproSession.ScenarioResult result)
	{
		return string.Join(Environment.NewLine,
			$"Run: {result.Name}",
			$"  tracked cycles: {result.TrackedCycles}",
			$"  alive native rows: {result.AliveNativeRows}/{result.TrackedCycles}",
			$"  alive cells: {result.AliveCells}/{result.TrackedCycles}",
			$"  alive renderers: {result.AliveRenderers}/{result.TrackedCycles}",
			$"  alive main text strings: {result.AliveMainTextStrings}/{result.TrackedCycles}",
			$"  alive detail text strings: {result.AliveDetailTextStrings}/{result.TrackedCycles}",
			$"  native rows with payload _mainTextText: {result.NativeRowsWithPayloadMainTextField}/{result.TrackedCycles}",
			$"  native rows with payload _detailTextText: {result.NativeRowsWithPayloadDetailTextField}/{result.TrackedCycles}",
			$"  native rows with payload main TextView.Text: {result.NativeRowsWithPayloadMainTextViewText}/{result.TrackedCycles}",
			$"  native rows with payload detail TextView.Text: {result.NativeRowsWithPayloadDetailTextViewText}/{result.TrackedCycles}",
			$"  retained BaseCellView field text bytes: {result.RetainedManagedFieldTextBytes:N0}",
			$"  estimated retained child TextView text bytes: {result.RetainedNativeTextViewBytes:N0}");
	}

	static string FormatBytes(long bytes)
	{
		if (bytes >= 1024 * 1024)
			return $"{bytes / 1024d / 1024d:N1} MiB";
		if (bytes >= 1024)
			return $"{bytes / 1024d:N1} KiB";
		return $"{bytes:N0} B";
	}
}
