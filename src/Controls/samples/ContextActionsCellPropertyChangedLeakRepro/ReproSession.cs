using System.Reflection;
using System.Runtime.CompilerServices;
using CoreGraphics;
using UIKit;

namespace ContextActionsCellPropertyChangedLeakRepro;

#pragma warning disable CS0618

internal static class ReproSession
{
	const int Cycles = 80;
	const int PayloadMegabytesPerCycle = 1;
	static readonly Type ContextActionsCellType = typeof(ListView).Assembly.GetType(
		"Microsoft.Maui.Controls.Handlers.Compatibility.ContextActionsCell",
		throwOnError: true)!;
	static readonly MethodInfo UpdateMethod = ContextActionsCellType.GetMethod(
		"Update",
		BindingFlags.Instance | BindingFlags.Public)!;

	public static ReproReport Run()
	{
		ForceFullGc();
		var baselineBytes = GC.GetTotalMemory(true);

		var control = RunControlDisposedNativeCells();
		var leak = RunLeakyDisposedContextActionCells();

		ForceFullGc();
		var finalBytes = GC.GetTotalMemory(true);

		return new ReproReport(
			Cycles,
			PayloadMegabytesPerCycle,
			baselineBytes,
			finalBytes,
			control,
			leak);
	}

	static ScenarioResult RunControlDisposedNativeCells()
	{
		var retainedCells = new List<Cell>();
		var tracked = new List<TrackedCycle>();

		for (var i = 0; i < Cycles; i++)
			CreateControlCycle(retainedCells, tracked, i);

		ForceFullGc();

		var result = ScenarioResult.From("retained MAUI cells with disposed native payload cells only", tracked);
		GC.KeepAlive(retainedCells);
		return result;
	}

	static ScenarioResult RunLeakyDisposedContextActionCells()
	{
		var retainedCells = new List<Cell>();
		var tracked = new List<TrackedCycle>();

		for (var i = 0; i < Cycles; i++)
			CreateLeakyCycle(retainedCells, tracked, i);

		ForceFullGc();

		var result = ScenarioResult.From("retained MAUI cells after disposed ContextActionsCell.Update", tracked);
		GC.KeepAlive(retainedCells);
		return result;
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	static void CreateControlCycle(List<Cell> retainedCells, List<TrackedCycle> tracked, int cycle)
	{
		var cell = CreateRetainedCell(cycle);
		retainedCells.Add(cell);

		var payload = new LeakPayload(cycle, PayloadMegabytesPerCycle * 1024L * 1024L);
		var nativeCell = new PayloadTableCell(cycle, payload);
		tracked.Add(TrackedCycle.ForControl(cycle, cell, nativeCell, payload));
		nativeCell.Dispose();
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	static void CreateLeakyCycle(List<Cell> retainedCells, List<TrackedCycle> tracked, int cycle)
	{
		var cell = CreateRetainedCell(cycle);
		retainedCells.Add(cell);

		var payload = new LeakPayload(cycle, PayloadMegabytesPerCycle * 1024L * 1024L);
		var nativeCell = new PayloadTableCell(cycle, payload);
		var contextCell = (UITableViewCell)Activator.CreateInstance(ContextActionsCellType)!;
		contextCell.Frame = new CGRect(0, 0, 360, 52);
		contextCell.ContentView.Frame = new CGRect(0, 0, 360, 52);

		using var tableView = new UITableView(new CGRect(0, 0, 360, 52));
		UpdateMethod.Invoke(contextCell, new object[] { tableView, cell, nativeCell });

		tracked.Add(TrackedCycle.ForLeak(cycle, cell, contextCell, nativeCell, payload));
		contextCell.Dispose();
	}

	static TextCell CreateRetainedCell(int cycle)
	{
		var cell = new TextCell
		{
			Text = $"Offline order {cycle + 1:000}",
			Detail = "Retained row cell with context actions"
		};

		cell.ContextActions.Add(new MenuItem
		{
			Text = "Archive",
			Command = new Command(() => { })
		});

		return cell;
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
}

internal sealed class PayloadTableCell : UITableViewCell
{
	public PayloadTableCell(int cycle, LeakPayload payload) : base(UITableViewCellStyle.Default, "payload")
	{
		Cycle = cycle;
		Payload = payload;
	}

	public int Cycle { get; }

	public LeakPayload Payload { get; }
}

internal sealed class LeakPayload
{
	public LeakPayload(int cycle, long payloadBytes)
	{
		Cycle = cycle;
		PayloadBytes = payloadBytes;
		DocumentBytes = new byte[payloadBytes];

		for (var i = 0; i < DocumentBytes.Length; i += 4096)
			DocumentBytes[i] = (byte)(cycle + i);

		CachedOrders = Enumerable.Range(1, 40)
			.Select(index => new CachedOrder(
				$"ORDER-{cycle + 1:000}-{index:000}",
				$"Offline fulfillment packet {index}",
				"Ready for sync"))
			.ToArray();
	}

	public int Cycle { get; }

	public long PayloadBytes { get; }

	public byte[] DocumentBytes { get; }

	public IReadOnlyList<CachedOrder> CachedOrders { get; }
}

internal sealed record CachedOrder(string Id, string Summary, string Status);

internal sealed record TrackedCycle(
	int Cycle,
	WeakReference RetainedCell,
	WeakReference? ContextActionCell,
	WeakReference NativeCell,
	WeakReference Payload,
	long PayloadBytes)
{
	public static TrackedCycle ForControl(int cycle, Cell retainedCell, PayloadTableCell nativeCell, LeakPayload payload)
	{
		return new TrackedCycle(
			cycle,
			new WeakReference(retainedCell),
			null,
			new WeakReference(nativeCell),
			new WeakReference(payload),
			payload.PayloadBytes);
	}

	public static TrackedCycle ForLeak(int cycle, Cell retainedCell, UITableViewCell contextActionCell, PayloadTableCell nativeCell, LeakPayload payload)
	{
		return new TrackedCycle(
			cycle,
			new WeakReference(retainedCell),
			new WeakReference(contextActionCell),
			new WeakReference(nativeCell),
			new WeakReference(payload),
			payload.PayloadBytes);
	}
}

internal sealed record ScenarioResult(
	string Name,
	int TrackedCycles,
	int AliveRetainedCells,
	int AliveContextActionCells,
	int AliveNativeCells,
	int AlivePayloads,
	long RetainedPayloadBytes)
{
	public static ScenarioResult From(string name, IReadOnlyList<TrackedCycle> cycles)
	{
		var aliveRetainedCells = 0;
		var aliveContextActionCells = 0;
		var aliveNativeCells = 0;
		var alivePayloads = 0;
		long retainedPayloadBytes = 0;

		foreach (var cycle in cycles)
		{
			if (cycle.RetainedCell.IsAlive)
				aliveRetainedCells++;

			if (cycle.ContextActionCell?.IsAlive == true)
				aliveContextActionCells++;

			if (cycle.NativeCell.IsAlive)
				aliveNativeCells++;

			if (cycle.Payload.IsAlive)
			{
				alivePayloads++;
				retainedPayloadBytes += cycle.PayloadBytes;
			}
		}

		return new ScenarioResult(
			name,
			cycles.Count,
			aliveRetainedCells,
			aliveContextActionCells,
			aliveNativeCells,
			alivePayloads,
			retainedPayloadBytes);
	}
}

internal sealed record ReproReport(
	int Cycles,
	int PayloadMegabytesPerCycle,
	long BaselineManagedBytes,
	long FinalManagedBytes,
	ScenarioResult Control,
	ScenarioResult Leak)
{
	public bool LeakProved =>
		Control.AliveRetainedCells == Control.TrackedCycles &&
		Control.AlivePayloads == 0 &&
		Control.AliveNativeCells == 0 &&
		Leak.AliveRetainedCells == Leak.TrackedCycles &&
		Leak.AliveContextActionCells == Leak.TrackedCycles &&
		Leak.AliveNativeCells == Leak.TrackedCycles &&
		Leak.AlivePayloads == Leak.TrackedCycles;

	public string ToText()
	{
		return string.Join(Environment.NewLine,
			"ContextActionsCell PropertyChanged leak repro",
			$"Cycles: {Cycles}",
			$"Payload per disposed native cell: {PayloadMegabytesPerCycle} MiB",
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
		var expectedPayload = result.TrackedCycles == 0 ? 0 : result.TrackedCycles * 1024L * 1024L;
		var retainedPercent = expectedPayload == 0 ? 0 : result.RetainedPayloadBytes * 100.0 / expectedPayload;

		return string.Join(Environment.NewLine,
			$"Scenario: {result.Name}",
			$"  Tracked cycles: {result.TrackedCycles}",
			$"  Retained MAUI cells alive: {result.AliveRetainedCells}/{result.TrackedCycles}",
			$"  Disposed ContextActionsCell instances alive: {result.AliveContextActionCells}/{result.TrackedCycles}",
			$"  Disposed native payload cells alive: {result.AliveNativeCells}/{result.TrackedCycles}",
			$"  Payloads alive: {result.AlivePayloads}/{result.TrackedCycles}",
			$"  Retained payload bytes: {FormatBytes(result.RetainedPayloadBytes)} ({retainedPercent:0.0}%)");
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

#pragma warning restore CS0618
