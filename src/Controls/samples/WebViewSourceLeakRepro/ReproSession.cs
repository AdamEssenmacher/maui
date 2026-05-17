using System.Diagnostics;

namespace WebViewSourceLeakRepro;

internal enum ReproMode
{
	SharedHtmlSource,
	FreshHtmlSourceControl,
	ClearSharedSourceOnDisappear
}

internal sealed record ReproOptions(
	ReproMode Mode,
	int Cycles,
	int PayloadMegabytesPerPage,
	int HtmlKilobytes,
	int DwellMilliseconds)
{
	public bool UsesSharedSource => Mode != ReproMode.FreshHtmlSourceControl;
	public bool ClearSourceOnDisappear => Mode == ReproMode.ClearSharedSourceOnDisappear;
	public long PayloadBytesPerPage => PayloadMegabytesPerPage * 1024L * 1024L;
	public string Name => Mode switch
	{
		ReproMode.SharedHtmlSource => "leaky shared HtmlWebViewSource",
		ReproMode.FreshHtmlSourceControl => "control: fresh HtmlWebViewSource per page",
		ReproMode.ClearSharedSourceOnDisappear => "mitigation: clear shared source",
		_ => Mode.ToString()
	};
}

internal sealed class ReproSession
{
	readonly List<TrackedCycle> _trackedCycles = new();
	readonly Stopwatch _elapsed = Stopwatch.StartNew();
	readonly HtmlWebViewSource? _sharedSource;
	int _currentCycle = -1;

	public ReproSession(ReproOptions options)
	{
		Options = options;

		if (options.UsesSharedSource)
		{
			_sharedSource = new HtmlWebViewSource
			{
				Html = ReproHtmlFactory.CreateHtml(0, options.HtmlKilobytes)
			};
		}
	}

	public static ReproSession? Current { get; set; }

	public ReproOptions Options { get; }

	public int CurrentCycle => _currentCycle;

	public int BeginNextCycle() => ++_currentCycle;

	public HtmlWebViewSource CreateSourceForCurrentCycle()
	{
		return _sharedSource ?? new HtmlWebViewSource
		{
			Html = ReproHtmlFactory.CreateHtml(CurrentCycle, Options.HtmlKilobytes)
		};
	}

	public void Track(ContentPage page, WebView webView, LeakPayloadViewModel payload)
	{
		_trackedCycles.Add(new TrackedCycle(
			CurrentCycle,
			new WeakReference(page),
			new WeakReference(webView),
			new WeakReference(payload),
			payload.PayloadBytes));
	}

	public ReproStats GetStats(MemorySnapshot baseline, MemorySnapshot current)
	{
		var alivePages = 0;
		var aliveWebViews = 0;
		var alivePayloads = 0;
		long retainedPayloadBytes = 0;

		foreach (var cycle in _trackedCycles)
		{
			if (cycle.Page.IsAlive)
				alivePages++;

			if (cycle.WebView.IsAlive)
				aliveWebViews++;

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
			aliveWebViews,
			alivePayloads,
			retainedPayloadBytes,
			baseline,
			current,
			_elapsed.Elapsed);
	}

	sealed record TrackedCycle(
		int Cycle,
		WeakReference Page,
		WeakReference WebView,
		WeakReference Payload,
		long PayloadBytes);
}

internal sealed class LeakPayloadViewModel
{
	public LeakPayloadViewModel(int cycle, long payloadBytes)
	{
		Cycle = cycle;
		PayloadBytes = payloadBytes;
		CachedDocumentBytes = new byte[payloadBytes];

		for (var i = 0; i < CachedDocumentBytes.Length; i += 4096)
			CachedDocumentBytes[i] = (byte)(cycle + i);

		RecentCases = Enumerable.Range(1, 60)
			.Select(index => new PortalCase(
				$"CASE-{cycle + 1:000}-{index:000}",
				$"Customer account package {index}",
				"Cached for offline review"))
			.ToArray();
	}

	public int Cycle { get; }

	public long PayloadBytes { get; }

	public byte[] CachedDocumentBytes { get; }

	public IReadOnlyList<PortalCase> RecentCases { get; }

	public string Title => $"Portal page {Cycle + 1}";
}

internal sealed record PortalCase(string Id, string Summary, string Status);

internal sealed record ReproStats(
	ReproOptions Options,
	int TrackedCycles,
	int AlivePages,
	int AliveWebViews,
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
			$"Shared source: {(Options.UsesSharedSource ? "yes" : "no")}",
			$"HTML document size: {Options.HtmlKilobytes} KB",
			$"Weak refs still alive after full GC:",
			$"  pages: {AlivePages}/{TrackedCycles}",
			$"  WebViews: {AliveWebViews}/{TrackedCycles}",
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
