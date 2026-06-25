using System.Diagnostics;

namespace ShellCanceledPushLeakRepro;

internal enum ReproMode
{
	CanceledPush,
	CreatedPageControl,
	CanceledPushThenSuccessfulNavigationCleanup
}

internal sealed record ReproOptions(
	ReproMode Mode,
	int Cycles,
	int PayloadMegabytesPerPage,
	int DwellMilliseconds)
{
	public bool UsesCanceledPush => Mode != ReproMode.CreatedPageControl;
	public bool CleanupAfterCanceledPushes => Mode == ReproMode.CanceledPushThenSuccessfulNavigationCleanup;
	public long PayloadBytesPerPage => PayloadMegabytesPerPage * 1024L * 1024L;
	public string Name => Mode switch
	{
		ReproMode.CanceledPush => "leaky canceled Shell Navigation.PushAsync",
		ReproMode.CreatedPageControl => "control: create pages without Shell push",
		ReproMode.CanceledPushThenSuccessfulNavigationCleanup => "mitigation: canceled pushes followed by successful Shell navigation",
		_ => Mode.ToString()
	};
}

internal sealed class ReproSession
{
	readonly List<TrackedCycle> _trackedCycles = new();
	readonly Stopwatch _elapsed = Stopwatch.StartNew();
	int _currentCycle = -1;

	public ReproSession(ReproOptions options)
	{
		Options = options;
	}

	public static ReproSession? Current { get; set; }

	public ReproOptions Options { get; }

	public int CurrentCycle => _currentCycle;

	public int BeginNextCycle() => ++_currentCycle;

	public LeakPage CreateTrackedPage()
	{
		var payload = new LeakPayloadViewModel(CurrentCycle, Options.PayloadBytesPerPage);
		var page = new LeakPage(payload);

		_trackedCycles.Add(new TrackedCycle(
			CurrentCycle,
			new WeakReference(page),
			new WeakReference(page.RootLayout),
			new WeakReference(payload),
			payload.PayloadBytes));

		return page;
	}

	public ReproStats GetStats(MemorySnapshot baseline, MemorySnapshot current)
	{
		var alivePages = 0;
		var aliveRootLayouts = 0;
		var alivePayloads = 0;
		long retainedPayloadBytes = 0;

		foreach (var cycle in _trackedCycles)
		{
			if (cycle.Page.IsAlive)
				alivePages++;

			if (cycle.RootLayout.IsAlive)
				aliveRootLayouts++;

			if (cycle.Payload.IsAlive)
			{
				alivePayloads++;
				retainedPayloadBytes += cycle.PayloadBytes;
			}
		}

		return new ReproStats(
			Options,
			_trackedCycles.Count,
			alivePages,
			aliveRootLayouts,
			alivePayloads,
			retainedPayloadBytes,
			baseline,
			current,
			_elapsed.Elapsed);
	}

	sealed record TrackedCycle(
		int Cycle,
		WeakReference Page,
		WeakReference RootLayout,
		WeakReference Payload,
		long PayloadBytes);
}

internal sealed class LeakPayloadViewModel
{
	public LeakPayloadViewModel(int cycle, long payloadBytes)
	{
		Cycle = cycle;
		PayloadBytes = payloadBytes;
		CachedRouteBytes = new byte[payloadBytes];

		for (var i = 0; i < CachedRouteBytes.Length; i += 4096)
			CachedRouteBytes[i] = (byte)(cycle + i);

		Rows = Enumerable.Range(1, 40)
			.Select(index => new RouteAuditRow(
				$"PUSH-{cycle + 1:000}-{index:000}",
				$"Canceled navigation audit row {index}",
				index % 2 == 0 ? "Queued" : "Blocked"))
			.ToArray();
	}

	public int Cycle { get; }

	public long PayloadBytes { get; }

	public byte[] CachedRouteBytes { get; }

	public IReadOnlyList<RouteAuditRow> Rows { get; }

	public string Title => $"Canceled push page {Cycle + 1}";
}

internal sealed record RouteAuditRow(string Id, string Summary, string Status);

internal sealed record ReproStats(
	ReproOptions Options,
	int TrackedCycles,
	int AlivePages,
	int AliveRootLayouts,
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
			$"Pages created: {TrackedCycles} in {Elapsed:mm\\:ss}",
			$"Canceled Shell pushes: {(Options.UsesCanceledPush ? "yes" : "no")}",
			$"Weak refs still alive after full GC:",
			$"  pages: {AlivePages}/{TrackedCycles}",
			$"  root layouts: {AliveRootLayouts}/{TrackedCycles}",
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

		if (value >= 1024L * 1024L * 1024L)
			return $"{sign}{value / 1024d / 1024d / 1024d:0.0} GB";

		if (value >= 1024L * 1024L)
			return $"{sign}{value / 1024d / 1024d:0.0} MB";

		if (value >= 1024L)
			return $"{sign}{value / 1024d:0.0} KB";

		return $"{sign}{value} B";
	}
}
