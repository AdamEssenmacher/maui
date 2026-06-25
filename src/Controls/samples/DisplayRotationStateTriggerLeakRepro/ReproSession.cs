using System.Diagnostics;

namespace DisplayRotationStateTriggerLeakRepro;

internal enum ReproMode
{
	StaticThemeControl,
	LiveThemeSwap,
	PreloadedThemeSwap
}

internal sealed record ReproOptions(
	ReproMode Mode,
	int Cycles,
	int PayloadMegabytesPerPage,
	int DwellMilliseconds)
{
	public bool SwapThemeAfterAttach => Mode == ReproMode.LiveThemeSwap;
	public bool PreloadSwappedTheme => Mode == ReproMode.PreloadedThemeSwap;
	public long PayloadBytesPerPage => PayloadMegabytesPerPage * 1024L * 1024L;
	public string Name => Mode switch
	{
		ReproMode.StaticThemeControl => "control: static rotation theme",
		ReproMode.LiveThemeSwap => "leaky live VSM replacement",
		ReproMode.PreloadedThemeSwap => "control: preloaded VSM replacement",
		_ => Mode.ToString()
	};
}

internal sealed class ReproSession
{
	readonly List<TrackedCycle> _trackedCycles = new();
	readonly Stopwatch _elapsed = Stopwatch.StartNew();
	TaskCompletionSource<bool>? _currentPageReady;
	int _currentCycle = -1;

	public ReproSession(ReproOptions options)
	{
		Options = options;
	}

	public static ReproSession? Current { get; set; }

	public ReproOptions Options { get; }

	public int CurrentCycle => _currentCycle;

	public int BeginNextCycle()
	{
		_currentPageReady = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
		return ++_currentCycle;
	}

	public Task WaitForCurrentPageReadyAsync(CancellationToken cancellationToken)
	{
		var readyTask = _currentPageReady?.Task ?? Task.CompletedTask;
		return readyTask.WaitAsync(cancellationToken);
	}

	public void MarkCurrentPageReady()
	{
		_currentPageReady?.TrySetResult(true);
		_currentPageReady = null;
	}

	public void Track(ContentPage page, VisualElement target, StateTriggerBase trigger, LeakPayloadViewModel payload)
	{
		_trackedCycles.Add(new TrackedCycle(
			CurrentCycle,
			new WeakReference(page),
			new WeakReference(target),
			new WeakReference(trigger),
			new WeakReference(payload),
			payload.PayloadBytes));
	}

	public ReproStats GetStats(MemorySnapshot baseline, MemorySnapshot current)
	{
		var alivePages = 0;
		var aliveTargets = 0;
		var aliveTriggers = 0;
		var alivePayloads = 0;
		long retainedPayloadBytes = 0;

		foreach (var cycle in _trackedCycles)
		{
			if (cycle.Page.IsAlive)
				alivePages++;

			if (cycle.Target.IsAlive)
				aliveTargets++;

			if (cycle.Trigger.IsAlive)
				aliveTriggers++;

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
			aliveTargets,
			aliveTriggers,
			alivePayloads,
			retainedPayloadBytes,
			baseline,
			current,
			_elapsed.Elapsed);
	}

	sealed record TrackedCycle(
		int Cycle,
		WeakReference Page,
		WeakReference Target,
		WeakReference Trigger,
		WeakReference Payload,
		long PayloadBytes);
}

internal sealed class LeakPayloadViewModel
{
	public LeakPayloadViewModel(int cycle, long payloadBytes)
	{
		Cycle = cycle;
		PayloadBytes = payloadBytes;
		CachedDashboardBytes = new byte[payloadBytes];

		for (var i = 0; i < CachedDashboardBytes.Length; i += 4096)
			CachedDashboardBytes[i] = (byte)(cycle + i);

		CachedReports = Enumerable.Range(1, 48)
			.Select(index => new CachedReport(
				$"RPT-{cycle + 1:000}-{index:000}",
				$"Rotation-aware dashboard report {index}",
				"Cached for offline device-layout review"))
			.ToArray();
	}

	public int Cycle { get; }

	public long PayloadBytes { get; }

	public byte[] CachedDashboardBytes { get; }

	public IReadOnlyList<CachedReport> CachedReports { get; }

	public string Title => $"Rotation-aware page {Cycle + 1}";
}

internal sealed record CachedReport(string Id, string Summary, string Status);

internal sealed record ReproStats(
	ReproOptions Options,
	int TrackedCycles,
	int AlivePages,
	int AliveTargets,
	int AliveTriggers,
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
			$"VSM groups replaced after attach: {(Options.SwapThemeAfterAttach ? "yes" : "no")}",
			$"Replacement VSM groups preloaded before attach: {(Options.PreloadSwappedTheme ? "yes" : "no")}",
			$"Weak refs still alive after full GC:",
			$"  pages: {AlivePages}/{TrackedCycles}",
			$"  target visual elements: {AliveTargets}/{TrackedCycles}",
			$"  old DisplayRotationStateTriggers: {AliveTriggers}/{TrackedCycles}",
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
