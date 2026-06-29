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

namespace AndroidSwitchCellViewCellRetentionRepro;

internal static class ReproSession
{
	internal const int Cycles = 96;
	const int BindingPayloadBytes = 1024 * 1024;

	static readonly BindingFlags InstanceNonPublic = BindingFlags.Instance | BindingFlags.NonPublic;
	static readonly FieldInfo CellField = typeof(BaseCellView).GetField("_cell", InstanceNonPublic)
		?? throw new MissingFieldException(typeof(BaseCellView).FullName, "_cell");
	static readonly FieldInfo MainTextField = typeof(BaseCellView).GetField("_mainTextText", InstanceNonPublic)
		?? throw new MissingFieldException(typeof(BaseCellView).FullName, "_mainTextText");
	static readonly FieldInfo MainTextViewField = typeof(BaseCellView).GetField("_mainText", InstanceNonPublic)
		?? throw new MissingFieldException(typeof(BaseCellView).FullName, "_mainText");

	static readonly List<SwitchCellView> RetainedNativeRows = new();

	public static async Task<ReproReport> RunAsync(IMauiContext context)
	{
		RetainedNativeRows.Clear();
		ForceFullGc();
		var baselineBytes = GC.GetTotalMemory(true);

		var control = await RunScenarioAsync(
			"control: clear SwitchCellView.Cell after disconnect",
			context,
			clearSwitchCellReference: true);

		var current = await RunScenarioAsync(
			"current: MAUI disconnect leaves SwitchCellView.Cell assigned",
			context,
			clearSwitchCellReference: false);

		ForceFullGc();
		var finalBytes = GC.GetTotalMemory(true);

		GC.KeepAlive(RetainedNativeRows);

		return new ReproReport(
			Cycles,
			BindingPayloadBytes,
			baselineBytes,
			finalBytes,
			control,
			current);
	}

	static async Task<ScenarioResult> RunScenarioAsync(
		string name,
		IMauiContext context,
		bool clearSwitchCellReference)
	{
		var tracked = new List<TrackedCycle>(Cycles);

		for (var i = 0; i < Cycles; i++)
		{
			CreateCycle(context, i, tracked, clearSwitchCellReference);

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
		bool clearSwitchCellReference)
	{
		var bindingPayload = new BindingPayload(cycle, BindingPayloadBytes);
		var cell = new SwitchCell
		{
			Text = $"Switch {cycle:D4}",
			On = cycle % 2 == 0,
			BindingContext = bindingPayload
		};

		var renderer = new SwitchCellRenderer();
		renderer.ParentView = new Grid { FlowDirection = FlowDirection.LeftToRight };
		renderer.SetMauiContext(context);
		cell.Handler = renderer;
		renderer.SetVirtualView(cell);

		if (renderer.PlatformView is not SwitchCellView nativeRow)
			throw new InvalidOperationException($"Expected {nameof(SwitchCellView)}, got {renderer.PlatformView?.GetType().FullName ?? "<null>"}.");

		if (!ReferenceEquals(nativeRow.Cell, cell))
			throw new InvalidOperationException("SwitchCellRenderer did not assign SwitchCellView.Cell.");

		((IElementHandler)renderer).DisconnectHandler();

		ClearKnownBaseCellState(nativeRow);

		if (clearSwitchCellReference)
			nativeRow.Cell = null!;

		RetainedNativeRows.Add(nativeRow);
		tracked.Add(TrackedCycle.Create(cycle, nativeRow, cell, renderer, bindingPayload));
	}

	static void ClearKnownBaseCellState(BaseCellView nativeRow)
	{
		CellField.SetValue(nativeRow, null);
		MainTextField.SetValue(nativeRow, null);

		if (MainTextViewField.GetValue(nativeRow) is TextView mainText)
			mainText.Text = string.Empty;

		nativeRow.ContentDescription = null;
	}

	static bool HasPayloadCellReference(SwitchCellView nativeRow)
	{
		return nativeRow.Cell?.BindingContext is BindingPayload;
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
		WeakReference<SwitchCellView> NativeRow,
		WeakReference<SwitchCell> Cell,
		WeakReference<SwitchCellRenderer> Renderer,
		WeakReference<BindingPayload> BindingPayload,
		WeakReference<byte[]> BindingPayloadBytes)
	{
		public static TrackedCycle Create(
			int cycle,
			SwitchCellView nativeRow,
			SwitchCell cell,
			SwitchCellRenderer renderer,
			BindingPayload bindingPayload)
		{
			return new TrackedCycle(
				cycle,
				new WeakReference<SwitchCellView>(nativeRow),
				new WeakReference<SwitchCell>(cell),
				new WeakReference<SwitchCellRenderer>(renderer),
				new WeakReference<BindingPayload>(bindingPayload),
				new WeakReference<byte[]>(bindingPayload.Payload));
		}
	}

	internal sealed record ScenarioResult(
		string Name,
		int TrackedCycles,
		int AliveNativeRows,
		int AliveCells,
		int AliveRenderers,
		int AliveBindingPayloads,
		int AliveBindingPayloadByteArrays,
		int NativeRowsWithPayloadCellReference,
		long RetainedBindingPayloadBytes)
	{
		internal static ScenarioResult From(string name, IReadOnlyList<TrackedCycle> tracked)
		{
			var aliveNativeRows = 0;
			var aliveCells = 0;
			var aliveRenderers = 0;
			var aliveBindingPayloads = 0;
			var aliveBindingPayloadByteArrays = 0;
			var nativeRowsWithPayloadCellReference = 0;
			long retainedBindingPayloadBytes = 0;

			foreach (var cycle in tracked)
			{
				if (cycle.NativeRow.TryGetTarget(out var nativeRow))
				{
					aliveNativeRows++;

					if (HasPayloadCellReference(nativeRow))
						nativeRowsWithPayloadCellReference++;
				}

				if (cycle.Cell.TryGetTarget(out _))
					aliveCells++;

				if (cycle.Renderer.TryGetTarget(out _))
					aliveRenderers++;

				if (cycle.BindingPayload.TryGetTarget(out _))
					aliveBindingPayloads++;

				if (cycle.BindingPayloadBytes.TryGetTarget(out _))
				{
					aliveBindingPayloadByteArrays++;
					retainedBindingPayloadBytes += BindingPayloadBytes;
				}
			}

			return new ScenarioResult(
				name,
				tracked.Count,
				aliveNativeRows,
				aliveCells,
				aliveRenderers,
				aliveBindingPayloads,
				aliveBindingPayloadByteArrays,
				nativeRowsWithPayloadCellReference,
				retainedBindingPayloadBytes);
		}
	}
}

internal sealed class BindingPayload
{
	public BindingPayload(int cycle, int payloadBytes)
	{
		Cycle = cycle;
		Payload = new byte[payloadBytes];
		Array.Fill(Payload, (byte)(cycle % 251));
	}

	public int Cycle { get; }

	public byte[] Payload { get; }
}

internal sealed record ReproReport(
	int Cycles,
	int BindingPayloadBytes,
	long BaselineManagedBytes,
	long FinalManagedBytes,
	ReproSession.ScenarioResult Control,
	ReproSession.ScenarioResult Current)
{
	public bool LeakProved =>
		Control.AliveNativeRows == Cycles &&
		Current.AliveNativeRows == Cycles &&
		Control.NativeRowsWithPayloadCellReference == 0 &&
		Control.AliveCells == 0 &&
		Control.AliveBindingPayloadByteArrays == 0 &&
		Current.NativeRowsWithPayloadCellReference == Cycles &&
		Current.AliveCells == Cycles &&
		Current.AliveBindingPayloads == Cycles &&
		Current.AliveBindingPayloadByteArrays == Cycles &&
		Current.AliveRenderers == 0;

	public string ToText()
	{
		var heapDeltaMiB = (FinalManagedBytes - BaselineManagedBytes) / 1024d / 1024d;

		return string.Join(Environment.NewLine,
			"AndroidSwitchCellViewCellRetentionRepro",
			$"Cycles: {Cycles}",
			$"Binding payload bytes per cycle: {BindingPayloadBytes:N0}",
			$"Baseline managed heap: {BaselineManagedBytes:N0} bytes",
			$"Final managed heap: {FinalManagedBytes:N0} bytes",
			$"Managed heap delta: {heapDeltaMiB:N1} MiB",
			$"Leak proved: {LeakProved}",
			string.Empty,
			Format(Control),
			string.Empty,
			Format(Current),
			string.Empty,
			$"Control retained payload: {FormatBytes(Control.RetainedBindingPayloadBytes)}",
			$"Current retained payload: {FormatBytes(Current.RetainedBindingPayloadBytes)}",
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
			$"  alive binding payloads: {result.AliveBindingPayloads}/{result.TrackedCycles}",
			$"  alive binding payload byte arrays: {result.AliveBindingPayloadByteArrays}/{result.TrackedCycles}",
			$"  native rows with payload SwitchCellView.Cell: {result.NativeRowsWithPayloadCellReference}/{result.TrackedCycles}",
			$"  retained binding payload bytes: {result.RetainedBindingPayloadBytes:N0}");
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
