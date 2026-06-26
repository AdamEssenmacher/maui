using System.Diagnostics;

namespace VisualElementResourcesLeakRepro;

internal enum ReproMode
{
	SharedResourcesDictionary,
	FreshResourcesDictionaryControl,
	ReplaceResourcesOnDisappear
}

internal sealed record ReproOptions(
	ReproMode Mode,
	int Cycles,
	int SharedResourceCount,
	int PayloadMegabytesPerPage,
	int DwellMilliseconds)
{
	public bool UsesSharedResourcesDictionary => Mode != ReproMode.FreshResourcesDictionaryControl;
	public bool ReplaceResourcesOnDisappear => Mode == ReproMode.ReplaceResourcesOnDisappear;
	public long PayloadBytesPerPage => PayloadMegabytesPerPage * 1024L * 1024L;
	public string Name => Mode switch
	{
		ReproMode.SharedResourcesDictionary => "leaky shared VisualElement.Resources dictionary",
		ReproMode.FreshResourcesDictionaryControl => "control: fresh Resources dictionary per page",
		ReproMode.ReplaceResourcesOnDisappear => "mitigation: replace Resources on disappearing",
		_ => Mode.ToString()
	};
}

internal sealed class ReproSession
{
	readonly List<TrackedCycle> _trackedCycles = new();
	readonly Stopwatch _elapsed = Stopwatch.StartNew();
	readonly ResourceDictionary? _sharedResourcesDictionary;
	int _currentCycle = -1;

	public ReproSession(ReproOptions options)
	{
		Options = options;

		if (options.UsesSharedResourcesDictionary)
			_sharedResourcesDictionary = CreateResourcesDictionary(options.SharedResourceCount, "shared");
	}

	public static ReproSession? Current { get; set; }

	public ReproOptions Options { get; }

	public int CurrentCycle => _currentCycle;

	public int BeginNextCycle() => ++_currentCycle;

	public ResourceDictionary CreateResourcesDictionary()
	{
		return _sharedResourcesDictionary ?? CreateResourcesDictionary(Options.SharedResourceCount, $"fresh-{CurrentCycle}");
	}

	public void Track(ContentPage page, Layout rootLayout, LeakPayloadViewModel payload)
	{
		_trackedCycles.Add(new TrackedCycle(
			CurrentCycle,
			new WeakReference(page),
			new WeakReference(rootLayout),
			new WeakReference(payload),
			payload.PayloadBytes));
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

	static ResourceDictionary CreateResourcesDictionary(int count, string prefix)
	{
		var dictionary = new ResourceDictionary
		{
			[$"{prefix}-Accent"] = Color.FromArgb("#2F6F73"),
			[$"{prefix}-Surface"] = Color.FromArgb("#F6F8FA")
		};

		for (var i = 0; i < count; i++)
			dictionary[$"{prefix}-Resource{i:000}"] = new ThemeAuditRow($"{prefix.ToUpperInvariant()}-{i:000}", $"Design token {i}", "Resources");

		return dictionary;
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
		CachedResourceBytes = new byte[payloadBytes];

		for (var i = 0; i < CachedResourceBytes.Length; i += 4096)
			CachedResourceBytes[i] = (byte)(cycle + i);

		Rows = Enumerable.Range(1, 40)
			.Select(index => new ThemeAuditRow(
				$"RES-{cycle + 1:000}-{index:000}",
				$"Resource-bound dashboard row {index}",
				index % 2 == 0 ? "Warm" : "Cold"))
			.ToArray();
	}

	public int Cycle { get; }

	public long PayloadBytes { get; }

	public byte[] CachedResourceBytes { get; }

	public IReadOnlyList<ThemeAuditRow> Rows { get; }

	public string Title => $"Resource dashboard {Cycle + 1}";
}

internal sealed record ThemeAuditRow(string Id, string Summary, string Status);

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
			$"Pages pushed and popped: {TrackedCycles} in {Elapsed:mm\\:ss}",
			$"Shared Resources dictionary: {(Options.UsesSharedResourcesDictionary ? "yes" : "no")}",
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
