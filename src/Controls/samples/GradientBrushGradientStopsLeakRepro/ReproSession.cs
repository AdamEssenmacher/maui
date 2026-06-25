using System.Diagnostics;

namespace GradientBrushGradientStopsLeakRepro;

internal enum ReproMode
{
	SharedGradientStops,
	FreshGradientStopsControl,
	ClearSharedGradientStopsOnDisappear
}

internal sealed record ReproOptions(
	ReproMode Mode,
	int Cycles,
	int BrushesPerPage,
	int StopsPerBrush,
	int PayloadMegabytesPerPage,
	int DwellMilliseconds)
{
	public bool UsesSharedGradientStops => Mode != ReproMode.FreshGradientStopsControl;
	public bool ClearGradientStopsOnDisappear => Mode == ReproMode.ClearSharedGradientStopsOnDisappear;
	public long PayloadBytesPerPage => PayloadMegabytesPerPage * 1024L * 1024L;
	public string Name => Mode switch
	{
		ReproMode.SharedGradientStops => "leaky shared GradientStopCollection",
		ReproMode.FreshGradientStopsControl => "control: fresh GradientStopCollection per Brush",
		ReproMode.ClearSharedGradientStopsOnDisappear => "mitigation: replace shared GradientStops",
		_ => Mode.ToString()
	};
}

internal sealed class ReproSession
{
	readonly List<TrackedCycle> _trackedCycles = new();
	readonly Stopwatch _elapsed = Stopwatch.StartNew();
	readonly GradientStopCollection? _sharedGradientStops;
	int _currentCycle = -1;

	public ReproSession(ReproOptions options)
	{
		Options = options;

		if (options.UsesSharedGradientStops)
			_sharedGradientStops = CreateGradientStops(options.StopsPerBrush);
	}

	public static ReproSession? Current { get; set; }

	public ReproOptions Options { get; }

	public int CurrentCycle => _currentCycle;

	public int BeginNextCycle() => ++_currentCycle;

	public GradientStopCollection CreateGradientStops()
	{
		return _sharedGradientStops ?? CreateGradientStops(Options.StopsPerBrush);
	}

	public void Track(ContentPage page, IReadOnlyList<Border> containers, IReadOnlyList<GradientBrush> brushes, LeakPayloadViewModel payload)
	{
		_trackedCycles.Add(new TrackedCycle(
			CurrentCycle,
			new WeakReference(page),
			containers.Select(container => new WeakReference(container)).ToArray(),
			brushes.Select(brush => new WeakReference(brush)).ToArray(),
			new WeakReference(payload),
			payload.PayloadBytes));
	}

	public ReproStats GetStats(MemorySnapshot baseline, MemorySnapshot current)
	{
		var alivePages = 0;
		var aliveContainers = 0;
		var aliveBrushes = 0;
		var totalContainers = 0;
		var totalBrushes = 0;
		var alivePayloads = 0;
		long retainedPayloadBytes = 0;

		foreach (var cycle in _trackedCycles)
		{
			if (cycle.Page.IsAlive)
				alivePages++;

			foreach (var container in cycle.Containers)
			{
				totalContainers++;

				if (container.IsAlive)
					aliveContainers++;
			}

			foreach (var brush in cycle.Brushes)
			{
				totalBrushes++;

				if (brush.IsAlive)
					aliveBrushes++;
			}

			if (cycle.Payload.IsAlive)
			{
				alivePayloads++;
				retainedPayloadBytes += cycle.PayloadBytes;
			}
		}

		return new ReproStats(
			Options,
			_trackedCycles.Count,
			totalContainers,
			totalBrushes,
			alivePages,
			aliveContainers,
			aliveBrushes,
			alivePayloads,
			retainedPayloadBytes,
			baseline,
			current,
			_elapsed.Elapsed);
	}

	static GradientStopCollection CreateGradientStops(int count)
	{
		var stops = new GradientStopCollection();
		var safeCount = Math.Max(2, count);

		for (var i = 0; i < safeCount; i++)
		{
			var offset = i / (float)(safeCount - 1);
			var color = (i % 4) switch
			{
				0 => Color.FromArgb("#22577A"),
				1 => Color.FromArgb("#38A3A5"),
				2 => Color.FromArgb("#57CC99"),
				_ => Color.FromArgb("#F4D35E")
			};

			stops.Add(new GradientStop(color, offset));
		}

		return stops;
	}

	sealed record TrackedCycle(
		int Cycle,
		WeakReference Page,
		IReadOnlyList<WeakReference> Containers,
		IReadOnlyList<WeakReference> Brushes,
		WeakReference Payload,
		long PayloadBytes);
}

internal sealed class LeakPayloadViewModel
{
	public LeakPayloadViewModel(int cycle, long payloadBytes, int brushCount)
	{
		Cycle = cycle;
		PayloadBytes = payloadBytes;
		CachedThemeBytes = new byte[payloadBytes];

		for (var i = 0; i < CachedThemeBytes.Length; i += 4096)
			CachedThemeBytes[i] = (byte)(cycle + i);

		ThemeRows = Enumerable.Range(1, brushCount * 12)
			.Select(index => new ThemeAuditRow(
				$"THEME-{cycle + 1:000}-{index:000}",
				$"Customer brand surface {index}",
				index % 2 == 0 ? "Published" : "Draft"))
			.ToArray();
	}

	public int Cycle { get; }

	public long PayloadBytes { get; }

	public byte[] CachedThemeBytes { get; }

	public IReadOnlyList<ThemeAuditRow> ThemeRows { get; }

	public string Title => $"Brand dashboard {Cycle + 1}";
}

internal sealed record ThemeAuditRow(string Id, string Summary, string Status);

internal sealed record ReproStats(
	ReproOptions Options,
	int TrackedCycles,
	int TotalContainers,
	int TotalBrushes,
	int AlivePages,
	int AliveContainers,
	int AliveBrushes,
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
			$"Containers created: {TotalContainers}",
			$"GradientBrushes created: {TotalBrushes}",
			$"Shared GradientStopCollection: {(Options.UsesSharedGradientStops ? "yes" : "no")}",
			$"Weak refs still alive after full GC:",
			$"  pages: {AlivePages}/{TrackedCycles}",
			$"  containers: {AliveContainers}/{TotalContainers}",
			$"  GradientBrushes: {AliveBrushes}/{TotalBrushes}",
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
