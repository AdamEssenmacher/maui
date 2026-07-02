using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.Maui.Controls.Internals;
using Microsoft.Maui.Controls.Compatibility.Platform.iOS;
using UIKit;

namespace LegacyTableViewModelRendererSourceRetentionRepro;

#pragma warning disable CS0618

internal static class ReproSession
{
	const int Cycles = 80;
	const int PayloadMegabytesPerCycle = 1;

	static readonly FieldInfo? ModelChangedField =
		typeof(TableView).GetField("ModelChanged", BindingFlags.Instance | BindingFlags.NonPublic);

	public static ReproReport Run()
	{
		ForceFullGc();
		var baselineBytes = GC.GetTotalMemory(true);

		var control = RunControlWithExplicitSubscriptionClear();
		var leak = RunLeakyLegacyNativeSources();

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

	static ScenarioResult RunControlWithExplicitSubscriptionClear()
	{
		var table = new TableView();
		var tracked = new List<TrackedCycle>();

		for (var i = 0; i < Cycles; i++)
			CreateControlCycle(table, tracked, i);

		ForceFullGc();

		var result = ScenarioResult.From("control: legacy sources cached headers but ModelChanged subscriptions were cleared", tracked);
		GC.KeepAlive(table);
		return result;
	}

	static ScenarioResult RunLeakyLegacyNativeSources()
	{
		var table = new TableView();
		var tracked = new List<TrackedCycle>();

		for (var i = 0; i < Cycles; i++)
			CreateLeakyCycle(table, tracked, i);

		ForceFullGc();

		var result = ScenarioResult.From("current: obsolete TableViewModelRenderer remains subscribed to ModelChanged", tracked);
		GC.KeepAlive(table);
		return result;
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	static void CreateControlCycle(TableView table, List<TrackedCycle> tracked, int cycle)
	{
		var payload = new LeakPayload(cycle, PayloadMegabytesPerCycle * 1024L * 1024L);
		var header = new PayloadHeaderCell(cycle, payload);

		table.Model = new HeaderPayloadTableModel(header);
		var source = CreateCachingSource(table);
		tracked.Add(TrackedCycle.Create(cycle, header, payload, source));

		ClearModelChangedSubscriptions(table);
		table.Model = EmptyTableModel.Instance;
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	static void CreateLeakyCycle(TableView table, List<TrackedCycle> tracked, int cycle)
	{
		var payload = new LeakPayload(cycle, PayloadMegabytesPerCycle * 1024L * 1024L);
		var header = new PayloadHeaderCell(cycle, payload);

		table.Model = new HeaderPayloadTableModel(header);
		var source = CreateCachingSource(table);
		tracked.Add(TrackedCycle.Create(cycle, header, payload, source));

		table.Model = EmptyTableModel.Instance;
	}

	static TableViewModelRenderer CreateCachingSource(TableView table)
	{
		var source = new TableViewModelRenderer(table);
		using var platformTable = new UITableView();
		_ = source.GetHeightForHeader(platformTable, 0);
		return source;
	}

	static void ClearModelChangedSubscriptions(TableView table)
	{
		if (ModelChangedField == null)
			throw new InvalidOperationException("Could not locate TableView.ModelChanged backing field.");

		ModelChangedField.SetValue(table, null);
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
	WeakReference HeaderCell,
	WeakReference Payload,
	WeakReference NativeSource,
	long PayloadBytes)
{
	public static TrackedCycle Create(
		int cycle,
		PayloadHeaderCell header,
		LeakPayload payload,
		TableViewModelRenderer source)
	{
		return new TrackedCycle(
			cycle,
			new WeakReference(header),
			new WeakReference(payload),
			new WeakReference(source),
			payload.PayloadBytes);
	}
}

internal sealed record ScenarioResult(
	string Name,
	int TrackedCycles,
	int AliveHeaders,
	int AlivePayloads,
	int AliveNativeSources,
	long RetainedPayloadBytes)
{
	public static ScenarioResult From(string name, IReadOnlyList<TrackedCycle> cycles)
	{
		var aliveHeaders = 0;
		var alivePayloads = 0;
		var aliveNativeSources = 0;
		long retainedPayloadBytes = 0;

		foreach (var cycle in cycles)
		{
			if (cycle.HeaderCell.IsAlive)
				aliveHeaders++;

			if (cycle.NativeSource.IsAlive)
				aliveNativeSources++;

			if (cycle.Payload.IsAlive)
			{
				alivePayloads++;
				retainedPayloadBytes += cycle.PayloadBytes;
			}
		}

		return new ScenarioResult(
			name,
			cycles.Count,
			aliveHeaders,
			alivePayloads,
			aliveNativeSources,
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
		Leak.AlivePayloads == Leak.TrackedCycles &&
		Leak.AliveHeaders == Leak.TrackedCycles &&
		Leak.AliveNativeSources == Leak.TrackedCycles;

	public string ToText()
	{
		return string.Join(Environment.NewLine,
			"LegacyTableViewModelRendererSourceRetentionRepro",
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
			$"  headers alive after full GC: {result.AliveHeaders}/{result.TrackedCycles}",
			$"  payloads alive after full GC: {result.AlivePayloads}/{result.TrackedCycles}",
			$"  native sources alive after full GC: {result.AliveNativeSources}/{result.TrackedCycles}",
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
