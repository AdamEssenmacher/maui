using System.Diagnostics;

namespace TableViewRootLeakRepro;

internal enum ReproMode
{
	SharedRoot,
	FreshRootControl,
	ClearSharedRootOnDisappear
}

internal sealed record ReproOptions(ReproMode Mode, int Cycles, int PayloadMegabytesPerPage, int DwellMilliseconds)
{
	public bool UsesSharedRoot => Mode != ReproMode.FreshRootControl;
	public bool ClearRootOnDisappear => Mode == ReproMode.ClearSharedRootOnDisappear;
	public long PayloadBytesPerPage => PayloadMegabytesPerPage * 1024L * 1024L;
	public string Name => Mode switch
	{
		ReproMode.SharedRoot => "leaky shared TableRoot",
		ReproMode.FreshRootControl => "control: fresh TableRoot per TableView",
		ReproMode.ClearSharedRootOnDisappear => "mitigation: clear shared TableView.Root",
		_ => Mode.ToString()
	};
}

internal sealed class ReproSession
{
	readonly List<TrackedCycle> _trackedCycles = new();
	readonly Stopwatch _elapsed = Stopwatch.StartNew();
	readonly TableRoot? _sharedRoot;
	int _currentCycle = -1;

	public ReproSession(ReproOptions options)
	{
		Options = options;
		if (options.UsesSharedRoot)
			_sharedRoot = CreateRoot();
	}

	public static ReproSession? Current { get; set; }
	public ReproOptions Options { get; }
	public int CurrentCycle => _currentCycle;
	public int BeginNextCycle() => ++_currentCycle;
	public TableRoot CreateTableRoot() => _sharedRoot ?? CreateRoot();

	public void Track(ContentPage page, TableView tableView, LeakPayloadViewModel payload)
	{
		_trackedCycles.Add(new TrackedCycle(
			new WeakReference(page),
			new WeakReference(tableView),
			new WeakReference(payload),
			payload.PayloadBytes));
	}

	public ReproStats GetStats(MemorySnapshot baseline, MemorySnapshot current)
	{
		var alivePages = 0;
		var aliveTables = 0;
		var alivePayloads = 0;
		long retainedPayloadBytes = 0;

		foreach (var cycle in _trackedCycles)
		{
			if (cycle.Page.IsAlive)
				alivePages++;

			if (cycle.TableView.IsAlive)
				aliveTables++;

			if (cycle.Payload.IsAlive)
			{
				alivePayloads++;
				retainedPayloadBytes += cycle.PayloadBytes;
			}
		}

		return new ReproStats(Options, _trackedCycles.Count, alivePages, aliveTables, alivePayloads, retainedPayloadBytes, baseline, current, _elapsed.Elapsed);
	}

	static TableRoot CreateRoot()
	{
		var root = new TableRoot("Shared fulfillment settings");

		for (var sectionIndex = 0; sectionIndex < 4; sectionIndex++)
		{
			var section = new TableSection($"Region group {sectionIndex + 1}");
			for (var rowIndex = 0; rowIndex < 20; rowIndex++)
				section.Add(new TextCell { Text = $"Warehouse setting {sectionIndex + 1}-{rowIndex + 1}", Detail = "Shared table metadata" });

			root.Add(section);
		}

		return root;
	}

	sealed record TrackedCycle(WeakReference Page, WeakReference TableView, WeakReference Payload, long PayloadBytes);
}

internal sealed class LeakPayloadViewModel
{
	public LeakPayloadViewModel(int cycle, long payloadBytes)
	{
		Cycle = cycle;
		PayloadBytes = payloadBytes;
		CachedSettingsBytes = new byte[payloadBytes];
		for (var i = 0; i < CachedSettingsBytes.Length; i += 4096)
			CachedSettingsBytes[i] = (byte)(cycle + i);
	}

	public int Cycle { get; }
	public long PayloadBytes { get; }
	public byte[] CachedSettingsBytes { get; }
	public string Title => $"Settings table {Cycle + 1}";
}

internal sealed record ReproStats(
	ReproOptions Options,
	int TrackedCycles,
	int AlivePages,
	int AliveTableViews,
	int AlivePayloads,
	long RetainedPayloadBytes,
	MemorySnapshot Baseline,
	MemorySnapshot Current,
	TimeSpan Elapsed)
{
	public string ToSummary()
	{
		var expectedPayload = Options.PayloadBytesPerPage * TrackedCycles;
		var retainedPercent = expectedPayload == 0 ? 0 : RetainedPayloadBytes * 100.0 / expectedPayload;

		return string.Join(Environment.NewLine,
			$"Run: {Options.Name}",
			$"Pages pushed and popped: {TrackedCycles} in {Elapsed:mm\\:ss}",
			$"Shared root: {(Options.UsesSharedRoot ? "yes" : "no")}",
			$"Weak refs still alive after full GC:",
			$"  pages: {AlivePages}/{TrackedCycles}",
			$"  TableViews: {AliveTableViews}/{TrackedCycles}",
			$"  payload view models: {AlivePayloads}/{TrackedCycles}",
			$"Payload retained by alive view models: {FormatBytes(RetainedPayloadBytes)} ({retainedPercent:0.0}% of allocated payload)",
			$"Managed heap delta after GC: {FormatBytes(Current.ManagedBytes - Baseline.ManagedBytes)}",
			$"GC heap delta after GC: {FormatBytes(Current.GcHeapBytes - Baseline.GcHeapBytes)}",
			$"Resident memory delta: {FormatBytes(Current.ResidentBytes - Baseline.ResidentBytes)}",
			$"Working set delta: {FormatBytes(Current.WorkingSetBytes - Baseline.WorkingSetBytes)}");
	}

	static string FormatBytes(long bytes)
	{
		var sign = bytes < 0 ? "-" : string.Empty;
		var value = Math.Abs(bytes);

		if (value >= 1024L * 1024L)
			return $"{sign}{value / 1024d / 1024d:0.0} MB";

		if (value >= 1024L)
			return $"{sign}{value / 1024d:0.0} KB";

		return $"{sign}{value} B";
	}
}
