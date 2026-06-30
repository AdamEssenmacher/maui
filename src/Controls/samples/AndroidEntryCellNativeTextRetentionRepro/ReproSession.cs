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

namespace AndroidEntryCellNativeTextRetentionRepro;

internal static class ReproSession
{
	const int Cycles = 512;
	const int PayloadCharsPerSlot = 8 * 1024;
	const int PayloadBytesPerSlot = PayloadCharsPerSlot * sizeof(char);
	const int ExpectedPayloadSlotsPerCycle = 3;

	static readonly BindingFlags InstanceNonPublic = BindingFlags.Instance | BindingFlags.NonPublic;
	static readonly FieldInfo CellField = typeof(EntryCellView).GetField("_cell", InstanceNonPublic)
		?? throw new MissingFieldException(typeof(EntryCellView).FullName, "_cell");
	static readonly FieldInfo LabelTextField = typeof(EntryCellView).GetField("_labelTextText", InstanceNonPublic)
		?? throw new MissingFieldException(typeof(EntryCellView).FullName, "_labelTextText");
	static readonly FieldInfo LabelViewField = typeof(EntryCellView).GetField("_label", InstanceNonPublic)
		?? throw new MissingFieldException(typeof(EntryCellView).FullName, "_label");

	static readonly List<EntryCellView> RetainedNativeRows = new();

	public static async Task<ReproReport> RunAsync(IMauiContext appContext)
	{
		RetainedNativeRows.Clear();
		ForceFullGc();
		var baselineBytes = GC.GetTotalMemory(true);

		var control = await RunScenarioAsync(
			"control: clear EntryCellView native label/placeholder strings after disconnect",
			appContext,
			clearNativeTextState: true);

		var current = await RunScenarioAsync(
			"current: MAUI disconnect leaves EntryCellView native label/placeholder strings assigned",
			appContext,
			clearNativeTextState: false);

		ForceFullGc();
		var finalBytes = GC.GetTotalMemory(true);

		GC.KeepAlive(RetainedNativeRows);

		return new ReproReport(
			Cycles,
			PayloadCharsPerSlot,
			PayloadBytesPerSlot,
			ExpectedPayloadSlotsPerCycle,
			baselineBytes,
			finalBytes,
			control,
			current);
	}

	static async Task<ScenarioResult> RunScenarioAsync(
		string name,
		IMauiContext appContext,
		bool clearNativeTextState)
	{
		var tracked = new List<TrackedCycle>(Cycles);

		for (var i = 0; i < Cycles; i++)
		{
			CreateCycle(appContext, i, tracked, clearNativeTextState);

			if (i % 32 == 0)
				await Task.Yield();
		}

		ForceFullGc();

		return ScenarioResult.From(name, tracked);
	}

	static void CreateCycle(
		IMauiContext appContext,
		int cycle,
		List<TrackedCycle> tracked,
		bool clearNativeTextState)
	{
		var cell = new EntryCell
		{
			Label = CreatePayload("label", cycle),
			Text = $"entry-text-{cycle:D4}",
			Placeholder = CreatePayload("placeholder", cycle)
		};

		var renderer = new EntryCellRenderer();
		renderer.ParentView = new Grid { FlowDirection = FlowDirection.LeftToRight };
		renderer.SetMauiContext(appContext);
		cell.Handler = renderer;
		renderer.SetVirtualView(cell);

		if (renderer.PlatformView is not EntryCellView nativeRow)
			throw new InvalidOperationException($"Expected {nameof(EntryCellView)}, got {renderer.PlatformView?.GetType().FullName ?? "<null>"}.");

		if (nativeRow.TextChanged?.Target is not EntryCellRenderer ||
			nativeRow.EditingCompleted?.Target is not EntryCellRenderer)
		{
			throw new InvalidOperationException("EntryCellRenderer did not assign renderer delegates to EntryCellView.");
		}

		var assignedBeforeCleanup = CaptureNativeTextState(nativeRow);

		((IElementHandler)renderer).DisconnectHandler();
		cell.Handler = null;

		ClearKnownNativeCellReference(nativeRow);
		ClearEntryCellDelegates(nativeRow);

		if (clearNativeTextState)
			ClearNativeTextState(nativeRow);

		RetainedNativeRows.Add(nativeRow);
		tracked.Add(TrackedCycle.Create(cycle, nativeRow, cell, renderer, assignedBeforeCleanup));
	}

	static void ClearKnownNativeCellReference(EntryCellView nativeRow)
	{
		CellField.SetValue(nativeRow, null);
	}

	static void ClearEntryCellDelegates(EntryCellView nativeRow)
	{
		nativeRow.TextChanged = null;
		nativeRow.FocusChanged = null;
		nativeRow.EditingCompleted = null;
	}

	static void ClearNativeTextState(EntryCellView nativeRow)
	{
		LabelTextField.SetValue(nativeRow, null);

		if (LabelViewField.GetValue(nativeRow) is TextView label)
			label.Text = string.Empty;

		nativeRow.EditText.Text = string.Empty;
		nativeRow.EditText.Hint = string.Empty;
	}

	static bool HasRendererDelegate(EntryCellView nativeRow)
	{
		return nativeRow.TextChanged?.Target is EntryCellRenderer ||
			nativeRow.EditingCompleted?.Target is EntryCellRenderer ||
			nativeRow.FocusChanged?.Target is EntryCellRenderer;
	}

	static NativeTextState CaptureNativeTextState(EntryCellView nativeRow)
	{
		var labelFieldLength = (LabelTextField.GetValue(nativeRow) as string)?.Length ?? 0;
		var labelNativeLength = LabelViewField.GetValue(nativeRow) is TextView label
			? label.Text?.Length ?? 0
			: 0;
		var editTextLength = nativeRow.EditText.Text?.Length ?? 0;
		var hintLength = nativeRow.EditText.Hint?.Length ?? 0;

		return new NativeTextState(
			labelFieldLength,
			labelNativeLength,
			editTextLength,
			hintLength);
	}

	static string CreatePayload(string kind, int cycle)
	{
		var prefix = $"android-entrycell-native-text-{kind}-{cycle:D4}-";
		return prefix + new string((char)('A' + (cycle % 26)), PayloadCharsPerSlot - prefix.Length);
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

	internal sealed record NativeTextState(
		int LabelFieldLength,
		int LabelNativeLength,
		int EditTextLength,
		int HintLength)
	{
		public int AssignedSlots =>
			(LabelFieldLength > 0 ? 1 : 0) +
			(LabelNativeLength > 0 ? 1 : 0) +
			(EditTextLength > 0 ? 1 : 0) +
			(HintLength > 0 ? 1 : 0);

		public int PayloadSlots =>
			(LabelFieldLength >= PayloadCharsPerSlot ? 1 : 0) +
			(LabelNativeLength >= PayloadCharsPerSlot ? 1 : 0) +
			(EditTextLength >= PayloadCharsPerSlot ? 1 : 0) +
			(HintLength >= PayloadCharsPerSlot ? 1 : 0);

		public long RetainedTextBytes =>
			((long)LabelFieldLength + LabelNativeLength + EditTextLength + HintLength) * sizeof(char);
	}

	internal sealed record TrackedCycle(
		int Cycle,
		WeakReference<EntryCellView> NativeRow,
		WeakReference<EntryCell> Cell,
		WeakReference<EntryCellRenderer> Renderer,
		NativeTextState AssignedBeforeCleanup)
	{
		public static TrackedCycle Create(
			int cycle,
			EntryCellView nativeRow,
			EntryCell cell,
			EntryCellRenderer renderer,
			NativeTextState assignedBeforeCleanup)
		{
			return new TrackedCycle(
				cycle,
				new WeakReference<EntryCellView>(nativeRow),
				new WeakReference<EntryCell>(cell),
				new WeakReference<EntryCellRenderer>(renderer),
				assignedBeforeCleanup);
		}
	}

	internal sealed record ScenarioResult(
		string Name,
		int TrackedCycles,
		int AliveNativeRows,
		int AliveCells,
		int AliveRenderers,
		int NativeRowsWithRendererDelegates,
		int AssignedSlotsBeforeCleanup,
		int PayloadSlotsBeforeCleanup,
		int AssignedLabelFieldSlots,
		int PayloadLabelFieldSlots,
		int AssignedNativeLabelSlots,
		int PayloadNativeLabelSlots,
		int AssignedEditTextSlots,
		int PayloadEditTextSlots,
		int AssignedHintSlots,
		int PayloadHintSlots,
		long RetainedNativeTextBytes)
	{
		internal static ScenarioResult From(string name, IReadOnlyList<TrackedCycle> tracked)
		{
			var aliveNativeRows = 0;
			var aliveCells = 0;
			var aliveRenderers = 0;
			var nativeRowsWithRendererDelegates = 0;
			var assignedSlotsBeforeCleanup = 0;
			var payloadSlotsBeforeCleanup = 0;
			var assignedLabelFieldSlots = 0;
			var payloadLabelFieldSlots = 0;
			var assignedNativeLabelSlots = 0;
			var payloadNativeLabelSlots = 0;
			var assignedEditTextSlots = 0;
			var payloadEditTextSlots = 0;
			var assignedHintSlots = 0;
			var payloadHintSlots = 0;
			long retainedNativeTextBytes = 0;

			foreach (var cycle in tracked)
			{
				assignedSlotsBeforeCleanup += cycle.AssignedBeforeCleanup.AssignedSlots;
				payloadSlotsBeforeCleanup += cycle.AssignedBeforeCleanup.PayloadSlots;

				if (cycle.NativeRow.TryGetTarget(out var nativeRow))
				{
					aliveNativeRows++;

					if (HasRendererDelegate(nativeRow))
						nativeRowsWithRendererDelegates++;

					var state = CaptureNativeTextState(nativeRow);
					if (state.LabelFieldLength > 0)
						assignedLabelFieldSlots++;
					if (state.LabelFieldLength >= PayloadCharsPerSlot)
						payloadLabelFieldSlots++;
					if (state.LabelNativeLength > 0)
						assignedNativeLabelSlots++;
					if (state.LabelNativeLength >= PayloadCharsPerSlot)
						payloadNativeLabelSlots++;
					if (state.EditTextLength > 0)
						assignedEditTextSlots++;
					if (state.EditTextLength >= PayloadCharsPerSlot)
						payloadEditTextSlots++;
					if (state.HintLength > 0)
						assignedHintSlots++;
					if (state.HintLength >= PayloadCharsPerSlot)
						payloadHintSlots++;

					retainedNativeTextBytes += state.RetainedTextBytes;
				}

				if (cycle.Cell.TryGetTarget(out _))
					aliveCells++;

				if (cycle.Renderer.TryGetTarget(out _))
					aliveRenderers++;
			}

			return new ScenarioResult(
				name,
				tracked.Count,
				aliveNativeRows,
				aliveCells,
				aliveRenderers,
				nativeRowsWithRendererDelegates,
				assignedSlotsBeforeCleanup,
				payloadSlotsBeforeCleanup,
				assignedLabelFieldSlots,
				payloadLabelFieldSlots,
				assignedNativeLabelSlots,
				payloadNativeLabelSlots,
				assignedEditTextSlots,
				payloadEditTextSlots,
				assignedHintSlots,
				payloadHintSlots,
				retainedNativeTextBytes);
		}
	}
}

internal sealed record ReproReport(
	int Cycles,
	int PayloadCharsPerSlot,
	int PayloadBytesPerSlot,
	int ExpectedPayloadSlotsPerCycle,
	long BaselineManagedBytes,
	long FinalManagedBytes,
	ReproSession.ScenarioResult Control,
	ReproSession.ScenarioResult Current)
{
	public int ExpectedPayloadSlots => Cycles * ExpectedPayloadSlotsPerCycle;

	public bool LeakProved =>
		Control.AliveNativeRows == Cycles &&
		Current.AliveNativeRows == Cycles &&
		Control.AliveCells == 0 &&
		Current.AliveCells == 0 &&
		Control.AliveRenderers == 0 &&
		Current.AliveRenderers == 0 &&
		Control.NativeRowsWithRendererDelegates == 0 &&
		Current.NativeRowsWithRendererDelegates == 0 &&
		Control.PayloadSlotsBeforeCleanup == ExpectedPayloadSlots &&
		Current.PayloadSlotsBeforeCleanup == ExpectedPayloadSlots &&
		Control.PayloadLabelFieldSlots == 0 &&
		Control.PayloadNativeLabelSlots == 0 &&
		Control.PayloadEditTextSlots == 0 &&
		Control.PayloadHintSlots == 0 &&
		Current.PayloadLabelFieldSlots == Cycles &&
		Current.PayloadNativeLabelSlots == Cycles &&
		Current.PayloadHintSlots == Cycles &&
		Current.RetainedNativeTextBytes >= 23L * 1024 * 1024;

	public string ToText()
	{
		var heapDeltaMiB = (FinalManagedBytes - BaselineManagedBytes) / 1024d / 1024d;

		return string.Join(Environment.NewLine,
			"AndroidEntryCellNativeTextRetentionRepro",
			$"Cycles: {Cycles}",
			$"Payload chars per retained label/placeholder slot: {PayloadCharsPerSlot:N0}",
			$"Payload bytes per retained label/placeholder slot: {PayloadBytesPerSlot:N0}",
			$"Expected payload slots: {ExpectedPayloadSlots:N0}",
			"Known graph roots neutralized in both runs: EntryCellView._cell, TextChanged, FocusChanged, EditingCompleted",
			$"Baseline managed heap: {BaselineManagedBytes:N0} bytes",
			$"Final managed heap: {FinalManagedBytes:N0} bytes",
			$"Managed heap delta: {heapDeltaMiB:N1} MiB",
			$"Leak proved: {LeakProved}",
			string.Empty,
			Format(Control),
			string.Empty,
			Format(Current),
			string.Empty,
			$"Control retained native text payload: {FormatBytes(Control.RetainedNativeTextBytes)}",
			$"Current retained native text payload: {FormatBytes(Current.RetainedNativeTextBytes)}",
			$"RESULT: {(LeakProved ? "PROVEN" : "NOT PROVEN")}");
	}

	string Format(ReproSession.ScenarioResult result)
	{
		return string.Join(Environment.NewLine,
			$"Run: {result.Name}",
			$"  tracked cycles: {result.TrackedCycles}",
			$"  alive native rows: {result.AliveNativeRows}/{result.TrackedCycles}",
			$"  alive EntryCells after full GC: {result.AliveCells}/{result.TrackedCycles}",
			$"  alive EntryCellRenderers after full GC: {result.AliveRenderers}/{result.TrackedCycles}",
			$"  native rows with renderer delegates: {result.NativeRowsWithRendererDelegates}/{result.TrackedCycles}",
			$"  payload slots assigned before cleanup: {result.PayloadSlotsBeforeCleanup}/{result.TrackedCycles * ExpectedPayloadSlotsPerCycle}",
			$"  assigned label backing-field slots: {result.AssignedLabelFieldSlots}/{result.TrackedCycles}",
			$"  payload-sized label backing-field slots: {result.PayloadLabelFieldSlots}/{result.TrackedCycles}",
			$"  assigned native label Text slots: {result.AssignedNativeLabelSlots}/{result.TrackedCycles}",
			$"  payload-sized native label Text slots: {result.PayloadNativeLabelSlots}/{result.TrackedCycles}",
			$"  assigned native EditText.Text slots: {result.AssignedEditTextSlots}/{result.TrackedCycles}",
			$"  payload-sized native EditText.Text slots: {result.PayloadEditTextSlots}/{result.TrackedCycles}",
			$"  assigned native EditText.Hint slots: {result.AssignedHintSlots}/{result.TrackedCycles}",
			$"  payload-sized native EditText.Hint slots: {result.PayloadHintSlots}/{result.TrackedCycles}",
			$"  retained native text bytes: {result.RetainedNativeTextBytes:N0}");
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
