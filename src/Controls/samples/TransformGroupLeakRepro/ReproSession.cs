using System.Diagnostics;
using Microsoft.Maui.Controls.Shapes;

namespace TransformGroupLeakRepro;

internal enum ReproMode
{
	SharedTransform,
	PrivateTransformControl,
	RemoveSharedTransformBeforeReplace
}

internal sealed record ReproOptions(
	ReproMode Mode,
	int Cycles,
	int PathsPerPage,
	int PayloadMegabytesPerPage,
	int DwellMilliseconds)
{
	public bool UsesSharedTransforms => Mode != ReproMode.PrivateTransformControl;
	public bool RemoveSharedTransformBeforeReplace => Mode == ReproMode.RemoveSharedTransformBeforeReplace;
	public long PayloadBytesPerPage => PayloadMegabytesPerPage * 1024L * 1024L;
	public string Name => Mode switch
	{
		ReproMode.SharedTransform => "leaky shared child ScaleTransform",
		ReproMode.PrivateTransformControl => "control: private child ScaleTransform per Path",
		ReproMode.RemoveSharedTransformBeforeReplace => "mitigation: remove shared child before replacing Children",
		_ => Mode.ToString()
	};
}

internal sealed class ReproSession
{
	readonly List<TrackedPage> _trackedPages = new();
	readonly Stopwatch _elapsed = Stopwatch.StartNew();
	readonly ScaleTransform[]? _sharedTransforms;
	int _currentCycle = -1;

	public ReproSession(ReproOptions options)
	{
		Options = options;

		if (options.UsesSharedTransforms)
		{
			_sharedTransforms = Enumerable.Range(0, options.PathsPerPage)
				.Select(index => new ScaleTransform
				{
					ScaleX = 1 + index * 0.002,
					ScaleY = 1 + index * 0.002
				})
				.ToArray();
		}
	}

	public static ReproSession? Current { get; set; }

	public ReproOptions Options { get; }

	public int CurrentCycle => _currentCycle;

	public int BeginNextCycle() => ++_currentCycle;

	public ScaleTransform CreateChildTransform(int pathIndex)
	{
		if (_sharedTransforms is null)
		{
			return new ScaleTransform
			{
				ScaleX = 1 + pathIndex * 0.002,
				ScaleY = 1 + pathIndex * 0.002
			};
		}

		return _sharedTransforms[pathIndex % _sharedTransforms.Length];
	}

	public void Track(ContentPage page, IReadOnlyList<Microsoft.Maui.Controls.Shapes.Path> paths, IReadOnlyList<TransformGroup> groups, LeakPayloadViewModel payload)
	{
		_trackedPages.Add(new TrackedPage(
			CurrentCycle,
			new WeakReference(page),
			paths.Select(path => new WeakReference(path)).ToArray(),
			groups.Select(group => new WeakReference(group)).ToArray(),
			new WeakReference(payload),
			payload.PayloadBytes));
	}

	public ReproStats GetStats(MemorySnapshot baseline, MemorySnapshot current)
	{
		var alivePages = 0;
		var alivePaths = 0;
		var aliveTransformGroups = 0;
		var alivePayloads = 0;
		var trackedPaths = 0;
		var trackedTransformGroups = 0;
		long retainedPayloadBytes = 0;

		foreach (var trackedPage in _trackedPages)
		{
			if (trackedPage.Page.IsAlive)
				alivePages++;

			foreach (var path in trackedPage.Paths)
			{
				trackedPaths++;

				if (path.IsAlive)
					alivePaths++;
			}

			foreach (var group in trackedPage.TransformGroups)
			{
				trackedTransformGroups++;

				if (group.IsAlive)
					aliveTransformGroups++;
			}

			if (trackedPage.Payload.IsAlive)
			{
				alivePayloads++;
				retainedPayloadBytes += trackedPage.PayloadBytes;
			}
		}

		return new ReproStats(
			Options,
			_trackedPages.Count,
			trackedPaths,
			trackedTransformGroups,
			alivePages,
			alivePaths,
			aliveTransformGroups,
			alivePayloads,
			retainedPayloadBytes,
			baseline,
			current,
			_elapsed.Elapsed);
	}

	sealed record TrackedPage(
		int Cycle,
		WeakReference Page,
		IReadOnlyList<WeakReference> Paths,
		IReadOnlyList<WeakReference> TransformGroups,
		WeakReference Payload,
		long PayloadBytes);
}

internal sealed class LeakPayloadViewModel
{
	public LeakPayloadViewModel(int cycle, long payloadBytes, int pathsPerPage)
	{
		Cycle = cycle;
		PayloadBytes = payloadBytes;
		CachedReportBytes = new byte[payloadBytes];

		for (var i = 0; i < CachedReportBytes.Length; i += 4096)
			CachedReportBytes[i] = (byte)(cycle + i);

		Metrics = Enumerable.Range(1, pathsPerPage)
			.Select(index => new DashboardMetric(
				$"METRIC-{cycle + 1:000}-{index:000}",
				$"Regional account signal {index}",
				40 + ((cycle + index) % 55)))
			.ToArray();
	}

	public int Cycle { get; }

	public long PayloadBytes { get; }

	public byte[] CachedReportBytes { get; }

	public IReadOnlyList<DashboardMetric> Metrics { get; }

	public string Title => $"Vector dashboard {Cycle + 1}";
}

internal sealed record DashboardMetric(string Id, string Summary, int Value);

internal sealed record ReproStats(
	ReproOptions Options,
	int TrackedPages,
	int TrackedPaths,
	int TrackedTransformGroups,
	int AlivePages,
	int AlivePaths,
	int AliveTransformGroups,
	int AlivePayloads,
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
			$"Shared child transforms: {(Options.UsesSharedTransforms ? "yes" : "no")}",
			$"Remove child before replacing Children: {(Options.RemoveSharedTransformBeforeReplace ? "yes" : "no")}",
			$"Weak refs still alive after full GC:",
			$"  pages: {AlivePages}/{TrackedPages}",
			$"  Paths: {AlivePaths}/{TrackedPaths}",
			$"  TransformGroups: {AliveTransformGroups}/{TrackedTransformGroups}",
			$"  payload view models: {AlivePayloads}/{TrackedPages}",
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
