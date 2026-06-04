using System.Collections.ObjectModel;
using System.Diagnostics;
using Microsoft.Maui;

namespace IndicatorViewItemsSourceLeakRepro;

internal enum ReproMode
{
	SharedObservableFeed,
	SnapshotListControl,
	ClearIndicatorOnDisappear
}

internal sealed record ReproOptions(
	ReproMode Mode,
	int Cycles,
	int FeedItems,
	int ControlPayloadMegabytesPerVisit,
	int PostGcFeedUpdates)
{
	public bool UseObservableFeed => Mode != ReproMode.SnapshotListControl;
	public bool ClearIndicatorOnDisappear => Mode == ReproMode.ClearIndicatorOnDisappear;
	public long ControlPayloadBytesPerVisit => ControlPayloadMegabytesPerVisit * 1024L * 1024L;
	public long IndicatorPayloadBytesPerVisit => ControlPayloadBytesPerVisit / 2;
	public long CarouselPayloadBytesPerVisit => ControlPayloadBytesPerVisit - IndicatorPayloadBytesPerVisit;

	public string Name => Mode switch
	{
		ReproMode.SharedObservableFeed => "leak: shared ObservableCollection feed",
		ReproMode.SnapshotListControl => "control: List snapshot feed",
		ReproMode.ClearIndicatorOnDisappear => "mitigation: clear IndicatorView.ItemsSource",
		_ => Mode.ToString()
	};
}

internal sealed class ReproSession
{
	readonly List<TrackedCycleHandle> _trackedCycles = new();
	readonly Stopwatch _elapsed = Stopwatch.StartNew();
	int _currentCycle = -1;

	public ReproSession(ReproOptions options)
	{
		Options = options;
		Feed = new SharedOperationsFeed(options.FeedItems);
	}

	public static ReproSession? Current { get; set; }

	public ReproOptions Options { get; }

	public SharedOperationsFeed Feed { get; }

	public int CurrentCycle => _currentCycle;

	public TimeSpan BaselineFeedUpdateElapsed { get; set; }

	public TimeSpan PostGcFeedUpdateElapsed { get; set; }

	public int BeginNextCycle() => ++_currentCycle;

	public TrackedCycleHandle Track(
		ContentPage page,
		IndicatorView indicatorView,
		CarouselView carouselView,
		VisitPayloadViewModel viewModel,
		RetainedPayloadBehavior indicatorPayload,
		RetainedPayloadBehavior carouselPayload)
	{
		var trackedCycle = new TrackedCycleHandle(
			CurrentCycle,
			new WeakReference(page),
			new WeakReference(indicatorView),
			new WeakReference(carouselView),
			new WeakReference(viewModel),
			new WeakReference(indicatorPayload),
			new WeakReference(carouselPayload),
			indicatorPayload.PayloadBytes,
			carouselPayload.PayloadBytes);

		_trackedCycles.Add(trackedCycle);
		return trackedCycle;
	}

	public TimeSpan MeasureFeedUpdateBurst(int updateCount)
	{
		if (updateCount <= 0)
			return TimeSpan.Zero;

		var stopwatch = Stopwatch.StartNew();
		Feed.ApplyLiveUpdateBurst(updateCount);
		stopwatch.Stop();
		return stopwatch.Elapsed;
	}

	public ReproStats GetStats(MemorySnapshot baseline, MemorySnapshot current, MemorySnapshot? beforePostGcUpdates = null)
	{
		var alivePages = 0;
		var aliveIndicators = 0;
		var aliveCarousels = 0;
		var aliveViewModels = 0;
		var aliveIndicatorPayloads = 0;
		var aliveCarouselPayloads = 0;
		var aliveIndicatorHandlers = 0;
		var aliveCarouselHandlers = 0;
		var aliveIndicatorPlatformViews = 0;
		var aliveCarouselPlatformViews = 0;
		long retainedControlPayloadBytes = 0;

		foreach (var cycle in _trackedCycles)
		{
			if (cycle.Page.IsAlive)
				alivePages++;

			if (cycle.IndicatorView.IsAlive)
				aliveIndicators++;

			if (cycle.CarouselView.IsAlive)
				aliveCarousels++;

			if (cycle.ViewModel.IsAlive)
				aliveViewModels++;

			if (cycle.IndicatorPayload.IsAlive)
			{
				aliveIndicatorPayloads++;
				retainedControlPayloadBytes += cycle.IndicatorPayloadBytes;
			}

			if (cycle.CarouselPayload.IsAlive)
			{
				aliveCarouselPayloads++;
				retainedControlPayloadBytes += cycle.CarouselPayloadBytes;
			}

			if (cycle.IndicatorHandler?.IsAlive == true)
				aliveIndicatorHandlers++;

			if (cycle.CarouselHandler?.IsAlive == true)
				aliveCarouselHandlers++;

			if (cycle.IndicatorPlatformView?.IsAlive == true)
				aliveIndicatorPlatformViews++;

			if (cycle.CarouselPlatformView?.IsAlive == true)
				aliveCarouselPlatformViews++;
		}

		return new ReproStats(
			Options,
			_trackedCycles.Count,
			alivePages,
			aliveIndicators,
			aliveCarousels,
			aliveViewModels,
			aliveIndicatorPayloads,
			aliveCarouselPayloads,
			aliveIndicatorHandlers,
			aliveCarouselHandlers,
			aliveIndicatorPlatformViews,
			aliveCarouselPlatformViews,
			retainedControlPayloadBytes,
			Feed.LiveCards.Count,
			BaselineFeedUpdateElapsed,
			PostGcFeedUpdateElapsed,
			baseline,
			beforePostGcUpdates ?? current,
			current,
			_elapsed.Elapsed);
	}
}

internal sealed record TrackedCycleHandle(
	int Cycle,
	WeakReference Page,
	WeakReference IndicatorView,
	WeakReference CarouselView,
	WeakReference ViewModel,
	WeakReference IndicatorPayload,
	WeakReference CarouselPayload,
	long IndicatorPayloadBytes,
	long CarouselPayloadBytes)
{
	public WeakReference? IndicatorHandler { get; private set; }

	public WeakReference? CarouselHandler { get; private set; }

	public WeakReference? IndicatorPlatformView { get; private set; }

	public WeakReference? CarouselPlatformView { get; private set; }

	public void CaptureIndicatorHandler(IElementHandler? handler)
	{
		if (handler is null)
			return;

		IndicatorHandler = new WeakReference(handler);
		CapturePlatformView(handler.PlatformView, platformView => IndicatorPlatformView = platformView);
	}

	public void CaptureCarouselHandler(IElementHandler? handler)
	{
		if (handler is null)
			return;

		CarouselHandler = new WeakReference(handler);
		CapturePlatformView(handler.PlatformView, platformView => CarouselPlatformView = platformView);
	}

	static void CapturePlatformView(object? platformView, Action<WeakReference> assign)
	{
		if (platformView is not null)
			assign(new WeakReference(platformView));
	}
}

internal sealed class SharedOperationsFeed
{
	readonly Random _random = new(7281);
	int _nextId;

	public SharedOperationsFeed(int initialCount)
	{
		for (var i = 0; i < initialCount; i++)
			LiveCards.Add(CreateCard());
	}

	public ObservableCollection<DashboardCard> LiveCards { get; } = new();

	public IReadOnlyList<DashboardCard> CreateSnapshot() => LiveCards.ToArray();

	public void ApplyLiveUpdateBurst(int updateCount)
	{
		for (var i = 0; i < updateCount; i++)
		{
			LiveCards.Add(CreateCard());

			if (LiveCards.Count > 0)
				LiveCards.RemoveAt(0);
		}
	}

	DashboardCard CreateCard()
	{
		var id = ++_nextId;
		var region = Regions[id % Regions.Length];
		var account = Accounts[id % Accounts.Length];
		var status = Statuses[id % Statuses.Length];
		var amount = 45000 + _random.Next(0, 950000);

		return new DashboardCard(
			id,
			$"{account} {id:000}",
			$"{region} pipeline health",
			status,
			amount);
	}

	static readonly string[] Regions =
	[
		"Northeast",
		"Midwest",
		"Southeast",
		"Mountain",
		"Pacific",
		"Canada"
	];

	static readonly string[] Accounts =
	[
		"Retail",
		"Logistics",
		"Clinical",
		"Energy",
		"Finance",
		"Public Sector"
	];

	static readonly string[] Statuses =
	[
		"Healthy",
		"Watch",
		"At risk",
		"Renewal",
		"Expansion"
	];
}

internal sealed record DashboardCard(int Id, string Title, string Subtitle, string Status, int Amount)
{
	public string AmountText => $"${Amount / 1000d:0.0}K";
}

internal sealed class VisitPayloadViewModel
{
	public VisitPayloadViewModel(int cycle)
	{
		Cycle = cycle;
	}

	public int Cycle { get; }

	public string Title => $"Operations page visit {Cycle + 1}";

	public string Description => "The page view model is tracked separately; retained bytes are attached to the controls.";
}

internal sealed record ReproStats(
	ReproOptions Options,
	int TrackedCycles,
	int AlivePages,
	int AliveIndicators,
	int AliveCarousels,
	int AliveViewModels,
	int AliveIndicatorPayloads,
	int AliveCarouselPayloads,
	int AliveIndicatorHandlers,
	int AliveCarouselHandlers,
	int AliveIndicatorPlatformViews,
	int AliveCarouselPlatformViews,
	long RetainedControlPayloadBytes,
	int CurrentFeedItems,
	TimeSpan BaselineFeedUpdateElapsed,
	TimeSpan PostGcFeedUpdateElapsed,
	MemorySnapshot Baseline,
	MemorySnapshot BeforePostGcUpdates,
	MemorySnapshot Current,
	TimeSpan Elapsed)
{
	public string ToSummary()
	{
		var expectedControlPayload = Options.ControlPayloadBytesPerVisit * TrackedCycles;
		var retainedPercent = expectedControlPayload == 0 ? 0 : RetainedControlPayloadBytes * 100.0 / expectedControlPayload;
		var aliveControlPayloads = AliveIndicatorPayloads + AliveCarouselPayloads;
		var expectedControlPayloads = TrackedCycles * 2;
		var updateMultiplier = BaselineFeedUpdateElapsed.TotalMilliseconds <= 0
			? 0
			: PostGcFeedUpdateElapsed.TotalMilliseconds / BaselineFeedUpdateElapsed.TotalMilliseconds;

		return string.Join(Environment.NewLine,
			$"Run: {Options.Name}",
			$"Pages pushed and popped: {TrackedCycles} in {Elapsed:mm\\:ss}",
			$"Shared feed items currently rooted: {CurrentFeedItems}",
			$"Weak refs still alive after full GC:",
			$"  pages: {AlivePages}/{TrackedCycles}",
			$"  IndicatorViews: {AliveIndicators}/{TrackedCycles}",
			$"  CarouselViews: {AliveCarousels}/{TrackedCycles}",
			$"  page view models: {AliveViewModels}/{TrackedCycles}",
			$"  control payload behaviors: {aliveControlPayloads}/{expectedControlPayloads}",
			$"    indicator payloads: {AliveIndicatorPayloads}/{TrackedCycles}",
			$"    carousel payloads: {AliveCarouselPayloads}/{TrackedCycles}",
			$"  handlers: IndicatorView {AliveIndicatorHandlers}/{TrackedCycles}, CarouselView {AliveCarouselHandlers}/{TrackedCycles}",
			$"  platform views: IndicatorView {AliveIndicatorPlatformViews}/{TrackedCycles}, CarouselView {AliveCarouselPlatformViews}/{TrackedCycles}",
			$"Retained control-attached payload: {FormatBytes(RetainedControlPayloadBytes)} ({retainedPercent:0.0}% of allocated control payload)",
			$"Feed update burst before pages: {FormatElapsed(BaselineFeedUpdateElapsed)}",
			$"Feed update burst after GC: {FormatElapsed(PostGcFeedUpdateElapsed)}{FormatMultiplier(updateMultiplier)}",
			$"Managed heap delta before update burst: {FormatBytes(BeforePostGcUpdates.ManagedBytes - Baseline.ManagedBytes)}",
			$"Managed heap delta after update burst: {FormatBytes(Current.ManagedBytes - Baseline.ManagedBytes)}",
			$"GC heap delta after update burst: {FormatBytes(Current.GcHeapBytes - Baseline.GcHeapBytes)}",
			$"Resident memory delta: {FormatBytes(Current.ResidentBytes - Baseline.ResidentBytes)}",
			$"Working set delta: {FormatBytes(Current.WorkingSetBytes - Baseline.WorkingSetBytes)}");
	}

	static string FormatElapsed(TimeSpan elapsed)
	{
		if (elapsed == TimeSpan.Zero)
			return "not measured";

		return $"{elapsed.TotalMilliseconds:0.0} ms";
	}

	static string FormatMultiplier(double multiplier)
	{
		if (multiplier <= 0)
			return string.Empty;

		return $" ({multiplier:0.0}x baseline)";
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
