using System.Runtime.CompilerServices;
using Microsoft.Maui.Controls.Handlers.Compatibility;
using Microsoft.Maui.Controls.Internals;
using UIKit;

namespace TableViewSourceGestureRetentionRepro;

#pragma warning disable CS0618

internal static class ReproSession
{
	const int Cycles = 80;
	const int PayloadMegabytesPerCycle = 1;

	public static readonly string ResultsPath =
		Path.Combine(Path.GetTempPath(), "tableview-source-gesture-retention-results.txt");

	public static ReproReport Run()
	{
		ForceFullGc();
		var baselineBytes = GC.GetTotalMemory(true);

		var control = RunControlWithoutGestureBinding();
		var leak = RunLeakyStaleGestureTargets();

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

	static ScenarioResult RunControlWithoutGestureBinding()
	{
		using var platformTable = new UITableView();
		var baselineGestureRecognizers = platformTable.GestureRecognizers?.Length ?? 0;
		var tracked = new List<TrackedCycle>();

		for (var i = 0; i < Cycles; i++)
			CreateControlCycle(platformTable, tracked, i);

		ForceFullGc();

		var result = ScenarioResult.From(
			"control: cache the same header payloads without binding native gestures",
			tracked,
			(platformTable.GestureRecognizers?.Length ?? 0) - baselineGestureRecognizers);

		GC.KeepAlive(platformTable);
		return result;
	}

	static ScenarioResult RunLeakyStaleGestureTargets()
	{
		using var platformTable = new UITableView();
		var baselineGestureRecognizers = platformTable.GestureRecognizers?.Length ?? 0;
		var tracked = new List<TrackedCycle>();

		for (var i = 0; i < Cycles; i++)
			CreateLeakyCycle(platformTable, tracked, i);

		ForceFullGc();

		var result = ScenarioResult.From(
			"current: replacement TableViewModelRenderer gestures remain attached to UITableView",
			tracked,
			(platformTable.GestureRecognizers?.Length ?? 0) - baselineGestureRecognizers);

		GC.KeepAlive(platformTable);
		return result;
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	static void CreateControlCycle(UITableView platformTable, List<TrackedCycle> tracked, int cycle)
	{
		var payload = new LeakPayload(cycle, PayloadMegabytesPerCycle * 1024L * 1024L);
		var header = new PayloadHeaderCell(cycle, payload);
		var table = new TableView
		{
			Model = new HeaderPayloadTableModel(header)
		};
		var source = CreateCachingSource(table, platformTable);

		tracked.Add(TrackedCycle.Create(cycle, table, source, header, payload));

		source.Dispose();
		table.Model = EmptyTableModel.Instance;
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	static void CreateLeakyCycle(UITableView platformTable, List<TrackedCycle> tracked, int cycle)
	{
		var payload = new LeakPayload(cycle, PayloadMegabytesPerCycle * 1024L * 1024L);
		var header = new PayloadHeaderCell(cycle, payload);
		var table = new TableView
		{
			Model = new HeaderPayloadTableModel(header)
		};
		var source = CreateBoundSource(table, platformTable);

		tracked.Add(TrackedCycle.Create(cycle, table, source, header, payload));
		table.Model = EmptyTableModel.Instance;
	}

	static TableViewModelRenderer CreateBoundSource(TableView table, UITableView platformTable)
	{
		var source = new TableViewModelRenderer(table);

		_ = source.NumberOfSections(platformTable);
		_ = source.GetHeightForHeader(platformTable, 0);

		return source;
	}

	static TableViewModelRenderer CreateCachingSource(TableView table, UITableView platformTable)
	{
		var source = new TableViewModelRenderer(table);

		_ = source.GetHeightForHeader(platformTable, 0);

		return source;
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

internal sealed class HeaderPayloadTableModel : TableModel
{
	readonly PayloadHeaderCell _header;

	public HeaderPayloadTableModel(PayloadHeaderCell header)
	{
		_header = header;
	}

	public override Cell GetHeaderCell(int section) => _header;

	public override object GetItem(int section, int row) => throw new IndexOutOfRangeException();

	public override int GetRowCount(int section) => 0;

	public override int GetSectionCount() => 1;

	public override string GetSectionTitle(int section) => $"Offline account package {_header.Cycle + 1}";
}

internal sealed class EmptyTableModel : TableModel
{
	public static readonly EmptyTableModel Instance = new();

	EmptyTableModel()
	{
	}

	public override object GetItem(int section, int row) => throw new IndexOutOfRangeException();

	public override int GetRowCount(int section) => 0;

	public override int GetSectionCount() => 0;
}

internal sealed class PayloadHeaderCell : TextCell
{
	public PayloadHeaderCell(int cycle, LeakPayload payload)
	{
		Cycle = cycle;
		Payload = payload;
		Text = $"Customer section {cycle + 1}";
		Detail = "Cached CRM summary with offline documents";
		Height = 44;
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

		Cases = Enumerable.Range(1, 20)
			.Select(index => new CustomerCase(
				$"CASE-{cycle + 1:000}-{index:000}",
				$"Enterprise onboarding review {index}",
				"Retained for offline triage"))
			.ToArray();
	}

	public int Cycle { get; }

	public long PayloadBytes { get; }

	public byte[] DocumentBytes { get; }

	public IReadOnlyList<CustomerCase> Cases { get; }
}

internal sealed record CustomerCase(string Id, string Summary, string Status);

internal sealed record TrackedCycle(
	int Cycle,
	WeakReference TableView,
	WeakReference NativeSource,
	WeakReference HeaderCell,
	WeakReference Payload,
	long PayloadBytes)
{
	public static TrackedCycle Create(
		int cycle,
		TableView table,
		TableViewModelRenderer source,
		PayloadHeaderCell header,
		LeakPayload payload)
	{
		return new TrackedCycle(
			cycle,
			new WeakReference(table),
			new WeakReference(source),
			new WeakReference(header),
			new WeakReference(payload),
			payload.PayloadBytes);
	}
}

internal sealed record ScenarioResult(
	string Name,
	int TrackedCycles,
	int AliveTableViews,
	int AliveNativeSources,
	int AliveHeaders,
	int AlivePayloads,
	int AdditionalGestureRecognizers,
	long RetainedPayloadBytes)
{
	public static ScenarioResult From(string name, IReadOnlyList<TrackedCycle> cycles, int additionalGestureRecognizers)
	{
		var aliveTableViews = 0;
		var aliveNativeSources = 0;
		var aliveHeaders = 0;
		var alivePayloads = 0;
		long retainedPayloadBytes = 0;

		foreach (var cycle in cycles)
		{
			if (cycle.TableView.IsAlive)
				aliveTableViews++;

			if (cycle.NativeSource.IsAlive)
				aliveNativeSources++;

			if (cycle.HeaderCell.IsAlive)
				aliveHeaders++;

			if (cycle.Payload.IsAlive)
			{
				alivePayloads++;
				retainedPayloadBytes += cycle.PayloadBytes;
			}
		}

		return new ScenarioResult(
			name,
			cycles.Count,
			aliveTableViews,
			aliveNativeSources,
			aliveHeaders,
			alivePayloads,
			additionalGestureRecognizers,
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
		Control.AlivePayloads == 0 &&
		Control.AliveHeaders == 0 &&
		Control.AliveNativeSources == 0 &&
		Control.AdditionalGestureRecognizers == 0 &&
		Leak.AlivePayloads == Leak.TrackedCycles &&
		Leak.AliveHeaders == Leak.TrackedCycles &&
		Leak.AliveNativeSources == Leak.TrackedCycles &&
		Leak.AdditionalGestureRecognizers == Leak.TrackedCycles * 2;

	public string ToText()
	{
		return string.Join(Environment.NewLine,
			"TableViewSourceGestureRetentionRepro",
			$"Result path: {ReproSession.ResultsPath}",
			$"Cycles: {Cycles}",
			$"Payload per cycle: {PayloadMegabytesPerCycle} MiB",
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
			$"Run: {result.Name}",
			$"  tracked cycles: {result.TrackedCycles}",
			$"  TableViews alive after full GC: {result.AliveTableViews}/{result.TrackedCycles}",
			$"  native sources alive after full GC: {result.AliveNativeSources}/{result.TrackedCycles}",
			$"  headers alive after full GC: {result.AliveHeaders}/{result.TrackedCycles}",
			$"  payloads alive after full GC: {result.AlivePayloads}/{result.TrackedCycles}",
			$"  additional native gesture recognizers: {result.AdditionalGestureRecognizers}",
			$"  retained payload bytes: {FormatBytes(result.RetainedPayloadBytes)} ({retainedPercent:0.0}%)");
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
