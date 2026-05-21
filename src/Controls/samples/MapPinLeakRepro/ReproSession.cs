using System.Diagnostics;
using Microsoft.Maui.Controls.Maps;

namespace MapPinLeakRepro;

internal enum ReproMode
{
	CurrentPinsControl,
	RemovedPinsLeak
}

internal sealed record ReproOptions(
	ReproMode Mode,
	int Cycles,
	int PinsPerPage,
	int DwellMilliseconds)
{
	public bool RemoveRetainedPinsBeforePageDisposal => Mode == ReproMode.RemovedPinsLeak;
	public string Name => Mode switch
	{
		ReproMode.CurrentPinsControl => "control: retained current pins",
		ReproMode.RemovedPinsLeak => "leak: retained removed pins",
		_ => Mode.ToString()
	};
}

internal sealed class ReproSession
{
	readonly List<TrackedCycle> _trackedCycles = new();
	readonly List<Pin> _retainedPins = new();
	readonly Stopwatch _elapsed = Stopwatch.StartNew();
	TaskCompletionSource? _currentPageReady;
	int _currentCycle = -1;

	public ReproSession(ReproOptions options)
	{
		Options = options;
	}

	public static ReproSession? Current { get; set; }

	public ReproOptions Options { get; }

	public int CurrentCycle => _currentCycle;

	public int RetainedPinsCount => _retainedPins.Count;

	public int BeginNextCycle()
	{
		_currentCycle++;
		_currentPageReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		return _currentCycle;
	}

	public async Task WaitForCurrentPageReadyAsync(CancellationToken token)
	{
		var ready = _currentPageReady ?? throw new InvalidOperationException("No page is being initialized.");
		await ready.Task.WaitAsync(token);
	}

	public void CompleteCurrentPageReady(Exception? error = null)
	{
		var ready = _currentPageReady;
		if (ready is null)
			return;

		if (error is null)
			ready.TrySetResult();
		else
			ready.TrySetException(error);
	}

	public void RetainPins(IEnumerable<Pin> pins)
	{
		_retainedPins.AddRange(pins);
	}

	public void Track(ContentPage page, Microsoft.Maui.Controls.Maps.Map map, object? mapHandler, PagePayload payload)
	{
		_trackedCycles.Add(new TrackedCycle(
			CurrentCycle,
			new WeakReference(page),
			new WeakReference(map),
			new WeakReference(mapHandler),
			new WeakReference(payload)));
	}

	public ReproStats GetStats(MemorySnapshot baseline, MemorySnapshot current)
	{
		var alivePages = 0;
		var aliveMaps = 0;
		var aliveHandlers = 0;
		var alivePayloads = 0;

		foreach (var cycle in _trackedCycles)
		{
			if (cycle.Page.IsAlive)
				alivePages++;

			if (cycle.Map.IsAlive)
				aliveMaps++;

			if (cycle.MapHandler.IsAlive)
				aliveHandlers++;

			if (cycle.Payload.IsAlive)
				alivePayloads++;
		}

		return new ReproStats(
			Options,
			_trackedCycles.Count,
			RetainedPinsCount,
			alivePages,
			aliveMaps,
			aliveHandlers,
			alivePayloads,
			baseline,
			current,
			_elapsed.Elapsed);
	}

	sealed record TrackedCycle(
		int Cycle,
		WeakReference Page,
		WeakReference Map,
		WeakReference MapHandler,
		WeakReference Payload);
}

internal sealed class PagePayload
{
	public PagePayload(int cycle)
	{
		Cycle = cycle;
		Title = $"Map page {cycle + 1}";
	}

	public int Cycle { get; }

	public string Title { get; }
}

internal sealed record ReproStats(
	ReproOptions Options,
	int TrackedCycles,
	int RetainedPins,
	int AlivePages,
	int AliveMaps,
	int AliveMapHandlers,
	int AlivePayloads,
	MemorySnapshot Baseline,
	MemorySnapshot Current,
	TimeSpan Elapsed)
{
	public string ToSummary()
	{
		return string.Join(Environment.NewLine,
			$"Run: {Options.Name}",
			$"Pages pushed and popped: {TrackedCycles} in {Elapsed:mm\\:ss}",
			$"Pins retained by session: {RetainedPins}",
			$"Pins were removed before page disposal: {(Options.RemoveRetainedPinsBeforePageDisposal ? "yes" : "no")}",
			$"Weak refs still alive after full GC:",
			$"  pages: {AlivePages}/{TrackedCycles}",
			$"  maps: {AliveMaps}/{TrackedCycles}",
			$"  map handlers: {AliveMapHandlers}/{TrackedCycles}",
			$"  page payloads: {AlivePayloads}/{TrackedCycles}",
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
