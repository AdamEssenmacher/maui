using System.Diagnostics;

namespace MapPoolLeakRepro;

internal enum ReproMode
{
	MapElements,
	NoElementsControl,
	ClearElementsOnDisappear
}

internal sealed record ReproOptions(
	ReproMode Mode,
	int Cycles,
	int MapElementsPerPage,
	int PayloadMegabytesPerPage,
	int DwellMilliseconds)
{
	public bool AddMapElements => Mode != ReproMode.NoElementsControl;
	public bool ClearElementsOnDisappear => Mode == ReproMode.ClearElementsOnDisappear;
	public long PayloadBytesPerPage => PayloadMegabytesPerPage * 1024L * 1024L;
	public string Name => Mode switch
	{
		ReproMode.MapElements => "leaky MapElements",
		ReproMode.NoElementsControl => "control: map without elements",
		ReproMode.ClearElementsOnDisappear => "mitigation: clear MapElements",
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

	public void Track(ContentPage page, View virtualMap, LeakPayloadViewModel payload, IReadOnlyList<object> mapElements)
	{
		_trackedCycles.Add(new TrackedCycle(
			CurrentCycle,
			new WeakReference(page),
			new WeakReference(virtualMap),
			new WeakReference(payload),
			mapElements.Select(static element => new WeakReference(element)).ToArray(),
			payload.PayloadBytes));
	}

	public ReproStats GetStats(MemorySnapshot baseline, MemorySnapshot current)
	{
		var alivePages = 0;
		var aliveVirtualMaps = 0;
		var alivePayloads = 0;
		var aliveElements = 0;
		long retainedPayloadBytes = 0;

		foreach (var cycle in _trackedCycles)
		{
			if (cycle.Page.IsAlive)
				alivePages++;

			if (cycle.VirtualMap.IsAlive)
				aliveVirtualMaps++;

			if (cycle.Payload.IsAlive)
			{
				alivePayloads++;
				retainedPayloadBytes += cycle.PayloadBytes;
			}

			aliveElements += cycle.MapElements.Count(static weakReference => weakReference.IsAlive);
		}

		return new ReproStats(
			Options,
			_trackedCycles.Count,
			alivePages,
			aliveVirtualMaps,
			alivePayloads,
			aliveElements,
			retainedPayloadBytes,
			baseline,
			current,
			_elapsed.Elapsed);
	}

	sealed record TrackedCycle(
		int Cycle,
		WeakReference Page,
		WeakReference VirtualMap,
		WeakReference Payload,
		IReadOnlyList<WeakReference> MapElements,
		long PayloadBytes);
}

internal sealed class LeakPayloadViewModel
{
	public LeakPayloadViewModel(int cycle, long payloadBytes)
	{
		Cycle = cycle;
		PayloadBytes = payloadBytes;
		Payload = new byte[payloadBytes];

		for (var i = 0; i < Payload.Length; i += 4096)
			Payload[i] = (byte)(cycle + i);
	}

	public int Cycle { get; }

	public long PayloadBytes { get; }

	public byte[] Payload { get; }

	public string Title => $"Cycle {Cycle}";
}

internal sealed record ReproStats(
	ReproOptions Options,
	int TrackedCycles,
	int AlivePages,
	int AliveVirtualMaps,
	int AlivePayloads,
	int AliveMapElements,
	long RetainedPayloadBytes,
	MemorySnapshot Baseline,
	MemorySnapshot Current,
	TimeSpan Elapsed)
{
	public string ToSummary()
	{
		var expectedPayload = Options.PayloadBytesPerPage * TrackedCycles;
		var expectedMapElements = Options.AddMapElements ? TrackedCycles * Options.MapElementsPerPage : 0;
		var retainedPercent = expectedPayload == 0 ? 0 : RetainedPayloadBytes * 100.0 / expectedPayload;

		return string.Join(Environment.NewLine,
			$"Run: {Options.Name}",
			$"Pages pushed and popped: {TrackedCycles} in {Elapsed:mm\\:ss}",
			$"Weak refs still alive after full GC:",
			$"  pages: {AlivePages}/{TrackedCycles}",
			$"  MAUI Map views: {AliveVirtualMaps}/{TrackedCycles}",
			$"  payload view models: {AlivePayloads}/{TrackedCycles}",
			$"  map elements: {AliveMapElements}/{expectedMapElements}",
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
