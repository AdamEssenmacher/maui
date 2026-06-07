using System.Diagnostics;
using Microsoft.Maui.Controls.Shapes;
using ShapePath = Microsoft.Maui.Controls.Shapes.Path;

namespace ShapeCollectionResetLeakRepro;

internal enum LeakTarget
{
	PathFigureSegments,
	PathGeometryFigures,
	GeometryGroupChildrenKnownIssue
}

internal enum ReproMode
{
	FreshItemsControl,
	SharedItemsClear,
	SharedItemsRemoveIndividually
}

internal static class LeakTargetExtensions
{
	public static string DisplayName(this LeakTarget target) => target switch
	{
		LeakTarget.PathFigureSegments => "PathFigure.Segments.Clear",
		LeakTarget.PathGeometryFigures => "PathGeometry.Figures.Clear",
		LeakTarget.GeometryGroupChildrenKnownIssue => "GeometryGroup.Children.Clear (#35795)",
		_ => target.ToString()
	};

	public static string OwnerName(this LeakTarget target) => target switch
	{
		LeakTarget.PathFigureSegments => "PathFigures",
		LeakTarget.PathGeometryFigures => "PathGeometries",
		LeakTarget.GeometryGroupChildrenKnownIssue => "GeometryGroups",
		_ => "shape owners"
	};

	public static string TransientItemName(this LeakTarget target) => target switch
	{
		LeakTarget.PathFigureSegments => "PathSegments",
		LeakTarget.PathGeometryFigures => "PathFigures",
		LeakTarget.GeometryGroupChildrenKnownIssue => "Geometries",
		_ => "items"
	};

	public static string ClearCall(this LeakTarget target) => target switch
	{
		LeakTarget.PathFigureSegments => "Segments.Clear()",
		LeakTarget.PathGeometryFigures => "Figures.Clear()",
		LeakTarget.GeometryGroupChildrenKnownIssue => "Children.Clear()",
		_ => "Clear()"
	};
}

internal sealed record ReproOptions(
	LeakTarget Target,
	ReproMode Mode,
	int Cycles,
	int PayloadMegabytesPerPage,
	int CardsPerPage,
	int SharedItemsPerCard,
	int DwellMilliseconds)
{
	public bool UsesSharedItems => Mode != ReproMode.FreshItemsControl;
	public bool RemoveItemsIndividually => Mode == ReproMode.SharedItemsRemoveIndividually;
	public long PayloadBytesPerPage => PayloadMegabytesPerPage * 1024L * 1024L;
	public int ExpectedTrackedVisuals => Cycles * CardsPerPage;
	public string Name => Mode switch
	{
		ReproMode.SharedItemsClear => $"leaky shared {Target.TransientItemName()} via {Target.ClearCall()}",
		ReproMode.FreshItemsControl => $"control: fresh page-local {Target.TransientItemName()}",
		ReproMode.SharedItemsRemoveIndividually => $"mitigation: remove shared {Target.TransientItemName()} individually",
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

	public void Track(ContentPage page, LeakPayloadViewModel payload, IEnumerable<TrackedShapeVisual> visuals)
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
				new WeakReference<object>(visual.TargetOwner)));
		}
	}

	public ReproStats GetStats(MemorySnapshot baseline, MemorySnapshot current)
	{
		var alivePages = 0;
		var alivePayloads = 0;
		var alivePaths = 0;
		var aliveOwners = 0;
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

			if (visual.TargetOwner.TryGetTarget(out _))
				aliveOwners++;
		}

		return new ReproStats(
			Options,
			_trackedPages.Count,
			_trackedVisuals.Count,
			alivePages,
			alivePayloads,
			alivePaths,
			aliveOwners,
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
		WeakReference<object> TargetOwner);
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

internal sealed record TrackedShapeVisual(ShapePath Path, object TargetOwner);

internal sealed record ReproStats(
	ReproOptions Options,
	int TrackedPages,
	int TrackedVisuals,
	int AlivePages,
	int AlivePayloads,
	int AlivePaths,
	int AliveOwners,
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
			$"Target: {Options.Target.DisplayName()}",
			$"Run: {Options.Name}",
			$"Pages pushed and popped: {TrackedPages} in {Elapsed:mm\\:ss}",
			$"Cards per page: {Options.CardsPerPage}",
			$"Tracked Path/{Options.Target.OwnerName()} pairs: {TrackedVisuals}",
			$"Shared transient {Options.Target.TransientItemName()} per card: {Options.SharedItemsPerCard}",
			$"Shared transient items: {(Options.UsesSharedItems ? "yes" : "no")}",
			$"Item removal: {(Options.RemoveItemsIndividually ? "RemoveAt" : "Clear")}",
			$"Weak refs still alive after full GC:",
			$"  pages: {AlivePages}/{TrackedPages}",
			$"  payload view models: {AlivePayloads}/{TrackedPages}",
			$"  Paths: {AlivePaths}/{TrackedVisuals}",
			$"  {Options.Target.OwnerName()}: {AliveOwners}/{TrackedVisuals}",
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
