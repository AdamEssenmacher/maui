#nullable enable

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Android.Content;
using Android.Widget;
using Microsoft.Maui;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Controls.Handlers.Compatibility;

namespace AndroidEntryCellViewDelegateRetentionRepro;

internal static class ReproSession
{
	internal const int Cycles = 96;
	const int ContextPayloadBytes = 512 * 1024;
	const int BindingPayloadBytes = 512 * 1024;
	const int TotalPayloadBytesPerCycle = ContextPayloadBytes + BindingPayloadBytes;

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

		var androidContext = appContext.Context
			?? throw new InvalidOperationException("The app MauiContext has no Android context.");

		var control = await RunScenarioAsync(
			"control: clear EntryCellView renderer delegates after disconnect",
			androidContext,
			clearEntryCellDelegates: true);

		var current = await RunScenarioAsync(
			"current: MAUI disconnect leaves EntryCellView renderer delegates assigned",
			androidContext,
			clearEntryCellDelegates: false);

		ForceFullGc();
		var finalBytes = GC.GetTotalMemory(true);

		GC.KeepAlive(RetainedNativeRows);

		return new ReproReport(
			Cycles,
			ContextPayloadBytes,
			BindingPayloadBytes,
			TotalPayloadBytesPerCycle,
			baselineBytes,
			finalBytes,
			control,
			current);
	}

	static async Task<ScenarioResult> RunScenarioAsync(
		string name,
		Context androidContext,
		bool clearEntryCellDelegates)
	{
		var tracked = new List<TrackedCycle>(Cycles);

		for (var i = 0; i < Cycles; i++)
		{
			CreateCycle(androidContext, i, tracked, clearEntryCellDelegates);

			if (i % 16 == 0)
				await Task.Yield();
		}

		ForceFullGc();

		return ScenarioResult.From(name, tracked);
	}

	static void CreateCycle(
		Context androidContext,
		int cycle,
		List<TrackedCycle> tracked,
		bool clearEntryCellDelegates)
	{
		var serviceProvider = new PayloadServiceProvider(cycle, ContextPayloadBytes);
		var cycleContext = new MauiContext(serviceProvider, androidContext);
		var bindingPayload = new BindingPayload(cycle, BindingPayloadBytes);
		var cell = new EntryCell
		{
			Label = $"Entry {cycle:D4}",
			Text = "short text",
			Placeholder = "short placeholder",
			BindingContext = bindingPayload
		};

		var renderer = new EntryCellRenderer();
		renderer.ParentView = new Grid { FlowDirection = FlowDirection.LeftToRight };
		renderer.SetMauiContext(cycleContext);
		cell.Handler = renderer;
		renderer.SetVirtualView(cell);

		if (renderer.PlatformView is not EntryCellView nativeRow)
			throw new InvalidOperationException($"Expected {nameof(EntryCellView)}, got {renderer.PlatformView?.GetType().FullName ?? "<null>"}.");

		if (nativeRow.TextChanged?.Target is not EntryCellRenderer ||
			nativeRow.EditingCompleted?.Target is not EntryCellRenderer)
		{
			throw new InvalidOperationException("EntryCellRenderer did not assign renderer delegates to EntryCellView.");
		}

		((IElementHandler)renderer).DisconnectHandler();

		ClearKnownNativeCellReference(nativeRow);
		ClearNativeTextState(nativeRow);

		if (clearEntryCellDelegates)
			ClearEntryCellDelegates(nativeRow);

		RetainedNativeRows.Add(nativeRow);
		tracked.Add(TrackedCycle.Create(cycle, nativeRow, cell, renderer, cycleContext, serviceProvider, bindingPayload));
	}

	static void ClearKnownNativeCellReference(EntryCellView nativeRow)
	{
		CellField.SetValue(nativeRow, null);
	}

	static void ClearNativeTextState(EntryCellView nativeRow)
	{
		LabelTextField.SetValue(nativeRow, null);

		if (LabelViewField.GetValue(nativeRow) is TextView label)
			label.Text = string.Empty;

		nativeRow.EditText.Text = string.Empty;
		nativeRow.EditText.Hint = string.Empty;
	}

	static void ClearEntryCellDelegates(EntryCellView nativeRow)
	{
		nativeRow.TextChanged = null;
		nativeRow.FocusChanged = null;
		nativeRow.EditingCompleted = null;
	}

	static bool HasRendererDelegate(EntryCellView nativeRow)
	{
		return nativeRow.TextChanged?.Target is EntryCellRenderer ||
			nativeRow.EditingCompleted?.Target is EntryCellRenderer;
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
		WeakReference<EntryCellView> NativeRow,
		WeakReference<EntryCell> Cell,
		WeakReference<EntryCellRenderer> Renderer,
		WeakReference<MauiContext> MauiContext,
		WeakReference<PayloadServiceProvider> ServiceProvider,
		WeakReference<byte[]> ContextPayload,
		WeakReference<BindingPayload> BindingPayload,
		WeakReference<byte[]> BindingPayloadBytes)
	{
		public static TrackedCycle Create(
			int cycle,
			EntryCellView nativeRow,
			EntryCell cell,
			EntryCellRenderer renderer,
			MauiContext mauiContext,
			PayloadServiceProvider serviceProvider,
			BindingPayload bindingPayload)
		{
			return new TrackedCycle(
				cycle,
				new WeakReference<EntryCellView>(nativeRow),
				new WeakReference<EntryCell>(cell),
				new WeakReference<EntryCellRenderer>(renderer),
				new WeakReference<MauiContext>(mauiContext),
				new WeakReference<PayloadServiceProvider>(serviceProvider),
				new WeakReference<byte[]>(serviceProvider.Payload),
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
		int AliveMauiContexts,
		int AliveServiceProviders,
		int AliveContextPayloads,
		int AliveBindingPayloads,
		int AliveBindingPayloadByteArrays,
		int NativeRowsWithRendererDelegates,
		long RetainedContextPayloadBytes,
		long RetainedBindingPayloadBytes)
	{
		internal static ScenarioResult From(string name, IReadOnlyList<TrackedCycle> tracked)
		{
			var aliveNativeRows = 0;
			var aliveCells = 0;
			var aliveRenderers = 0;
			var aliveMauiContexts = 0;
			var aliveServiceProviders = 0;
			var aliveContextPayloads = 0;
			var aliveBindingPayloads = 0;
			var aliveBindingPayloadByteArrays = 0;
			var nativeRowsWithRendererDelegates = 0;
			long retainedContextPayloadBytes = 0;
			long retainedBindingPayloadBytes = 0;

			foreach (var cycle in tracked)
			{
				if (cycle.NativeRow.TryGetTarget(out var nativeRow))
				{
					aliveNativeRows++;

					if (HasRendererDelegate(nativeRow))
						nativeRowsWithRendererDelegates++;
				}

				if (cycle.Cell.TryGetTarget(out _))
					aliveCells++;

				if (cycle.Renderer.TryGetTarget(out _))
					aliveRenderers++;

				if (cycle.MauiContext.TryGetTarget(out _))
					aliveMauiContexts++;

				if (cycle.ServiceProvider.TryGetTarget(out _))
					aliveServiceProviders++;

				if (cycle.ContextPayload.TryGetTarget(out _))
				{
					aliveContextPayloads++;
					retainedContextPayloadBytes += ContextPayloadBytes;
				}

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
				aliveMauiContexts,
				aliveServiceProviders,
				aliveContextPayloads,
				aliveBindingPayloads,
				aliveBindingPayloadByteArrays,
				nativeRowsWithRendererDelegates,
				retainedContextPayloadBytes,
				retainedBindingPayloadBytes);
		}
	}
}

internal sealed class PayloadServiceProvider : IServiceProvider
{
	public PayloadServiceProvider(int cycle, int payloadBytes)
	{
		Cycle = cycle;
		Payload = new byte[payloadBytes];
		Array.Fill(Payload, (byte)(cycle % 251));
	}

	public int Cycle { get; }

	public byte[] Payload { get; }

	public object? GetService(Type serviceType)
	{
		return null;
	}
}

internal sealed class BindingPayload
{
	public BindingPayload(int cycle, int payloadBytes)
	{
		Cycle = cycle;
		Payload = new byte[payloadBytes];
		Array.Fill(Payload, (byte)((cycle + 97) % 251));
	}

	public int Cycle { get; }

	public byte[] Payload { get; }
}

internal sealed record ReproReport(
	int Cycles,
	int ContextPayloadBytes,
	int BindingPayloadBytes,
	int TotalPayloadBytesPerCycle,
	long BaselineManagedBytes,
	long FinalManagedBytes,
	ReproSession.ScenarioResult Control,
	ReproSession.ScenarioResult Current)
{
	public bool LeakProved =>
		Control.AliveNativeRows == Cycles &&
		Current.AliveNativeRows == Cycles &&
		Control.NativeRowsWithRendererDelegates == 0 &&
		Control.AliveRenderers == 0 &&
		Control.AliveMauiContexts == 0 &&
		Control.AliveContextPayloads == 0 &&
		Control.AliveBindingPayloadByteArrays == 0 &&
		Current.NativeRowsWithRendererDelegates == Cycles &&
		Current.AliveRenderers == Cycles &&
		Current.AliveMauiContexts == Cycles &&
		Current.AliveServiceProviders == Cycles &&
		Current.AliveContextPayloads == Cycles &&
		Current.AliveCells == Cycles &&
		Current.AliveBindingPayloads == Cycles &&
		Current.AliveBindingPayloadByteArrays == Cycles;

	public string ToText()
	{
		var heapDeltaMiB = (FinalManagedBytes - BaselineManagedBytes) / 1024d / 1024d;
		var currentTotalBytes = Current.RetainedContextPayloadBytes + Current.RetainedBindingPayloadBytes;

		return string.Join(Environment.NewLine,
			"AndroidEntryCellViewDelegateRetentionRepro",
			$"Cycles: {Cycles}",
			$"Context payload bytes per cycle: {ContextPayloadBytes:N0}",
			$"Binding payload bytes per cycle: {BindingPayloadBytes:N0}",
			$"Total payload bytes per cycle: {TotalPayloadBytesPerCycle:N0}",
			$"Baseline managed heap: {BaselineManagedBytes:N0} bytes",
			$"Final managed heap: {FinalManagedBytes:N0} bytes",
			$"Managed heap delta: {heapDeltaMiB:N1} MiB",
			$"Leak proved: {LeakProved}",
			string.Empty,
			Format(Control),
			string.Empty,
			Format(Current),
			string.Empty,
			$"Control retained payload: {FormatBytes(Control.RetainedContextPayloadBytes + Control.RetainedBindingPayloadBytes)}",
			$"Current retained payload: {FormatBytes(currentTotalBytes)}",
			$"Current retained context payload: {FormatBytes(Current.RetainedContextPayloadBytes)}",
			$"Current retained binding payload: {FormatBytes(Current.RetainedBindingPayloadBytes)}",
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
			$"  alive MauiContexts: {result.AliveMauiContexts}/{result.TrackedCycles}",
			$"  alive service providers: {result.AliveServiceProviders}/{result.TrackedCycles}",
			$"  alive context payload byte arrays: {result.AliveContextPayloads}/{result.TrackedCycles}",
			$"  alive binding payloads: {result.AliveBindingPayloads}/{result.TrackedCycles}",
			$"  alive binding payload byte arrays: {result.AliveBindingPayloadByteArrays}/{result.TrackedCycles}",
			$"  native rows with renderer delegates: {result.NativeRowsWithRendererDelegates}/{result.TrackedCycles}",
			$"  retained context payload bytes: {result.RetainedContextPayloadBytes:N0}",
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
