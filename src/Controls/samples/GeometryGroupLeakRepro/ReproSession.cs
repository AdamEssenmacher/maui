using System.Diagnostics;
using Microsoft.Maui.Controls.Shapes;
using ShapePath = Microsoft.Maui.Controls.Shapes.Path;

namespace GeometryGroupLeakRepro;

internal enum ReproMode
{
	SharedFragments,
	FreshFragmentsControl,
	RemoveSharedFragmentsIndividually
}

internal sealed record ReproOptions(
	ReproMode Mode,
	int Cycles,
	int PayloadMegabytesPerPage,
	int CardsPerPage,
	int SharedFragmentsPerCard,
	int DwellMilliseconds)
{
	public bool UsesSharedFragments => Mode != ReproMode.FreshFragmentsControl;
	public bool RemoveFragmentsIndividually => Mode == ReproMode.RemoveSharedFragmentsIndividually;
	public long PayloadBytesPerPage => PayloadMegabytesPerPage * 1024L * 1024L;
	public int ExpectedTrackedVisuals => Cycles * CardsPerPage;
	public string Name => Mode switch
	{
		ReproMode.SharedFragments => "leaky shared Geometry fragments",
		ReproMode.FreshFragmentsControl => "control: fresh page-local Geometry fragments",
		ReproMode.RemoveSharedFragmentsIndividually => "mitigation: remove shared fragments individually",
		_ => Mode.ToString()
	};
}

internal sealed class ReproSession
{
	readonly List<TrackedPage> _trackedPages = new();
	readonly List<TrackedVisual> _trackedVisuals = new();
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

	public void Track(ContentPage page, LeakPayloadViewModel payload, IEnumerable<TrackedGeometryVisual> visuals)
	{
		_trackedPages.Add(new TrackedPage(
			CurrentCycle,
			new WeakReference<ContentPage>(page),
			new WeakReference<LeakPayloadViewModel>(payload),
			payload.PayloadBytes));

		foreach (var visual in visuals)
		{
			_trackedVisuals.Add(new TrackedVisual(
				CurrentCycle,
				new WeakReference<ShapePath>(visual.Path),
				new WeakReference<GeometryGroup>(visual.GeometryGroup)));
		}
	}

	public ReproStats GetStats(MemorySnapshot baseline, MemorySnapshot current)
	{
		var alivePages = 0;
		var alivePayloads = 0;
		var alivePaths = 0;
		var aliveGeometryGroups = 0;
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

		foreach (var visual in _trackedVisuals)
		{
			if (visual.Path.TryGetTarget(out _))
				alivePaths++;

			if (visual.GeometryGroup.TryGetTarget(out _))
				aliveGeometryGroups++;
		}

		return new ReproStats(
			Options,
			_trackedPages.Count,
			_trackedVisuals.Count,
			alivePages,
			alivePayloads,
			alivePaths,
			aliveGeometryGroups,
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

	sealed record TrackedVisual(
		int Cycle,
		WeakReference<ShapePath> Path,
		WeakReference<GeometryGroup> GeometryGroup);
}

internal sealed class LeakPayloadViewModel
{
	public LeakPayloadViewModel(int cycle, long payloadBytes)
	{
		Cycle = cycle;
		PayloadBytes = payloadBytes;
		CachedCaseFileBytes = new byte[checked((int)payloadBytes)];

		for (var i = 0; i < CachedCaseFileBytes.Length; i += 4096)
			CachedCaseFileBytes[i] = (byte)(cycle + i);

		RecentCases = Enumerable.Range(1, 80)
			.Select(index => new OperationsCase(
				$"CASE-{cycle + 1:000}-{index:000}",
				$"Regional claims packet {index}",
				index % 4 == 0 ? "Needs review" : "Ready offline"))
			.ToArray();
	}

	public int Cycle { get; }

	public long PayloadBytes { get; }

	public byte[] CachedCaseFileBytes { get; }

	public IReadOnlyList<OperationsCase> RecentCases { get; }

	public string Title => $"Operations dashboard {Cycle + 1}";
}

internal sealed record OperationsCase(string Id, string Summary, string Status);

internal sealed record TrackedGeometryVisual(ShapePath Path, GeometryGroup GeometryGroup);

internal sealed record ReproStats(
	ReproOptions Options,
	int TrackedPages,
	int TrackedVisuals,
	int AlivePages,
	int AlivePayloads,
	int AlivePaths,
	int AliveGeometryGroups,
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
			$"Cards per page: {Options.CardsPerPage}",
			$"Tracked Path/GeometryGroup pairs: {TrackedVisuals}",
			$"Shared transient fragments per card: {Options.SharedFragmentsPerCard}",
			$"Shared fragments: {(Options.UsesSharedFragments ? "yes" : "no")}",
			$"Fragment removal: {(Options.RemoveFragmentsIndividually ? "RemoveAt" : "Clear")}",
			$"Weak refs still alive after full GC:",
			$"  pages: {AlivePages}/{TrackedPages}",
			$"  payload view models: {AlivePayloads}/{TrackedPages}",
			$"  Paths: {AlivePaths}/{TrackedVisuals}",
			$"  GeometryGroups: {AliveGeometryGroups}/{TrackedVisuals}",
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
