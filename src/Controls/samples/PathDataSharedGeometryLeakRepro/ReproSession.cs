using System.Diagnostics;
using ShapePath = Microsoft.Maui.Controls.Shapes.Path;

namespace PathDataSharedGeometryLeakRepro;

internal enum ReproMode
{
	SharedSources,
	FreshSourcesControl,
	ClearPathSourcesOnDisappear
}

internal sealed record ReproOptions(
	ReproMode Mode,
	int Cycles,
	int PayloadMegabytesPerPage,
	int PathsPerPage,
	int DwellMilliseconds)
{
	public bool UsesSharedSources => Mode != ReproMode.FreshSourcesControl;
	public bool ClearPathSourcesOnDisappear => Mode == ReproMode.ClearPathSourcesOnDisappear;
	public long PayloadBytesPerPage => PayloadMegabytesPerPage * 1024L * 1024L;
	public int ExpectedTrackedPaths => Cycles * PathsPerPage;
	public string Name => Mode switch
	{
		ReproMode.SharedSources => "leaky: shared app-level PathGeometry and ScaleTransform",
		ReproMode.FreshSourcesControl => "control: fresh page-local PathGeometry and ScaleTransform",
		ReproMode.ClearPathSourcesOnDisappear => "mitigation: clear Path.Data and Path.RenderTransform",
		_ => Mode.ToString()
	};
}

internal sealed class ReproSession
{
	readonly List<TrackedPage> _trackedPages = new();
	readonly List<TrackedPath> _trackedPaths = new();
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

	public void Track(ContentPage page, LeakPayloadViewModel payload, IEnumerable<TrackedPathVisual> visuals)
	{
		_trackedPages.Add(new TrackedPage(
			CurrentCycle,
			new WeakReference<ContentPage>(page),
			new WeakReference<LeakPayloadViewModel>(payload),
			payload.PayloadBytes));

		foreach (var visual in visuals)
		{
			_trackedPaths.Add(new TrackedPath(
				CurrentCycle,
				new WeakReference<ShapePath>(visual.Path)));
		}
	}

	public ReproStats GetStats(MemorySnapshot baseline, MemorySnapshot current)
	{
		var alivePages = 0;
		var alivePayloads = 0;
		var alivePaths = 0;
		long retainedPayloadBytes = 0;

		foreach (var page in _trackedPages)
		{
			if (page.Page.TryGetTarget(out _))
				alivePages++;

			if (page.Payload.TryGetTarget(out _))
			{
				alivePayloads++;
				retainedPayloadBytes += page.PayloadBytes;
			}
		}

		foreach (var trackedPath in _trackedPaths)
		{
			if (trackedPath.Path.TryGetTarget(out _))
				alivePaths++;
		}

		return new ReproStats(
			Options,
			_trackedPages.Count,
			_trackedPaths.Count,
			alivePages,
			alivePayloads,
			alivePaths,
			retainedPayloadBytes,
			baseline,
			current,
			_elapsed.Elapsed);
	}

	sealed record TrackedPage(
		int Cycle,
		WeakReference<ContentPage> Page,
		WeakReference<LeakPayloadViewModel> Payload,
		long PayloadBytes);

	sealed record TrackedPath(
		int Cycle,
		WeakReference<ShapePath> Path);
}

internal sealed class LeakPayloadViewModel
{
	public LeakPayloadViewModel(int cycle, long payloadBytes)
	{
		Cycle = cycle;
		PayloadBytes = payloadBytes;
		CachedDashboardSnapshot = new byte[checked((int)payloadBytes)];

		for (var i = 0; i < CachedDashboardSnapshot.Length; i += 4096)
			CachedDashboardSnapshot[i] = (byte)(cycle + i);

		Panels = Enumerable.Range(1, 96)
			.Select(index => new DashboardPanel(
				$"PANEL-{cycle + 1:000}-{index:000}",
				$"Offline analytics tile {index} with cached trend and incident data",
				index % 6 == 0 ? "Escalated" : "Healthy"))
			.ToArray();
	}

	public int Cycle { get; }

	public long PayloadBytes { get; }

	public byte[] CachedDashboardSnapshot { get; }

	public IReadOnlyList<DashboardPanel> Panels { get; }

	public string Title => $"Operations dashboard {Cycle + 1}";
}

internal sealed record DashboardPanel(string Id, string Summary, string Status);

internal sealed record TrackedPathVisual(ShapePath Path);

internal sealed record ReproStats(
	ReproOptions Options,
	int TrackedPages,
	int TrackedPaths,
	int AlivePages,
	int AlivePayloads,
	int AlivePaths,
	long RetainedPayloadBytes,
	MemorySnapshot Baseline,
	MemorySnapshot Current,
	TimeSpan Elapsed)
{
	public string ToSummary()
	{
		var expectedPayload = Options.PayloadBytesPerPage * TrackedPages;
		var retainedPercent = expectedPayload == 0 ? 0 : RetainedPayloadBytes * 100.0 / expectedPayload;

		return string.Join(Environment.NewLine,
			$"Run: {Options.Name}",
			$"Pages pushed and popped: {TrackedPages} in {Elapsed:mm\\:ss}",
			$"Paths per page: {Options.PathsPerPage}",
			$"Tracked Paths: {TrackedPaths}",
			$"Shared app-level sources: {(Options.UsesSharedSources ? "yes" : "no")}",
			$"Cleared Path.Data and Path.RenderTransform on disappearing: {(Options.ClearPathSourcesOnDisappear ? "yes" : "no")}",
			$"Weak refs still alive after full GC:",
			$"  pages: {AlivePages}/{TrackedPages}",
			$"  payload view models: {AlivePayloads}/{TrackedPages}",
			$"  Paths: {AlivePaths}/{TrackedPaths}",
			$"Payload retained by alive view models: {FormatBytes(RetainedPayloadBytes)} ({retainedPercent:0.0}% of allocated payload)",
			$"Expected direct payload if fully retained: {FormatBytes(expectedPayload)}",
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
