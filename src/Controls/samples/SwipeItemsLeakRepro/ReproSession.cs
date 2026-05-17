using System.Diagnostics;

namespace SwipeItemsLeakRepro;

internal enum ReproMode
{
	CachedSwipeItems,
	OwnedSwipeItemsControl,
	ReplaceRightItemsOnDisappear
}

internal sealed record ReproOptions(
	ReproMode Mode,
	int Cycles,
	int RowsPerPage,
	int PayloadKilobytesPerRow,
	int DwellMilliseconds)
{
	public bool CacheSwipeItems => Mode != ReproMode.OwnedSwipeItemsControl;
	public bool ReplaceRightItemsOnDisappear => Mode == ReproMode.ReplaceRightItemsOnDisappear;
	public long PayloadBytesPerRow => PayloadKilobytesPerRow * 1024L;
	public long PayloadBytesPerPage => RowsPerPage * PayloadBytesPerRow;

	public string Name => Mode switch
	{
		ReproMode.CachedSwipeItems => "leaky cached SwipeItems",
		ReproMode.OwnedSwipeItemsControl => "control: owned SwipeItems",
		ReproMode.ReplaceRightItemsOnDisappear => "failed unsubscribe: replace RightItems",
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

	public void Track(
		ContentPage page,
		WorkOrderBoardViewModel board,
		IReadOnlyList<SwipeView> swipeViews,
		IReadOnlyList<SwipeItems> swipeItems,
		IReadOnlyList<WorkOrderRowViewModel> rows)
	{
		_trackedCycles.Add(new TrackedCycle(
			CurrentCycle,
			new WeakReference(page),
			new WeakReference(board),
			swipeViews.Select(static swipeView => new WeakReference(swipeView)).ToArray(),
			swipeItems.Select(static items => new WeakReference(items)).ToArray(),
			rows.Select(static row => new WeakReference(row)).ToArray(),
			board.PayloadBytes));
	}

	public ReproStats GetStats(MemorySnapshot baseline, MemorySnapshot current)
	{
		var alivePages = 0;
		var aliveBoards = 0;
		var aliveSwipeViews = 0;
		var aliveSwipeItems = 0;
		var aliveRows = 0;
		long retainedPayloadBytes = 0;

		foreach (var cycle in _trackedCycles)
		{
			if (cycle.Page.IsAlive)
				alivePages++;

			if (cycle.Board.IsAlive)
			{
				aliveBoards++;
				retainedPayloadBytes += cycle.PayloadBytes;
			}

			aliveSwipeViews += cycle.SwipeViews.Count(static weakReference => weakReference.IsAlive);
			aliveSwipeItems += cycle.SwipeItems.Count(static weakReference => weakReference.IsAlive);
			aliveRows += cycle.Rows.Count(static weakReference => weakReference.IsAlive);
		}

		return new ReproStats(
			Options,
			_trackedCycles.Count,
			alivePages,
			aliveBoards,
			aliveSwipeViews,
			aliveSwipeItems,
			aliveRows,
			retainedPayloadBytes,
			SharedSwipeActionCache.CachedSetCount,
			baseline,
			current,
			_elapsed.Elapsed);
	}

	sealed record TrackedCycle(
		int Cycle,
		WeakReference Page,
		WeakReference Board,
		IReadOnlyList<WeakReference> SwipeViews,
		IReadOnlyList<WeakReference> SwipeItems,
		IReadOnlyList<WeakReference> Rows,
		long PayloadBytes);
}

internal sealed class WorkOrderBoardViewModel
{
	public WorkOrderBoardViewModel(int cycle, int rowCount, long payloadBytesPerRow)
	{
		Cycle = cycle;
		Rows = Enumerable.Range(0, rowCount)
			.Select(index => new WorkOrderRowViewModel(cycle, index, payloadBytesPerRow))
			.ToArray();
		PayloadBytes = Rows.Sum(static row => row.PayloadBytes);
	}

	public int Cycle { get; }

	public IReadOnlyList<WorkOrderRowViewModel> Rows { get; }

	public long PayloadBytes { get; }

	public string Title => $"Dispatch board {Cycle + 1}";
}

internal sealed class WorkOrderRowViewModel
{
	static readonly string[] Customers =
	{
		"Contoso Medical",
		"Northwind Logistics",
		"Fabrikam Field Ops",
		"Coho Winery",
		"Tailspin Energy",
		"Adventure Works"
	};

	public WorkOrderRowViewModel(int cycle, int row, long payloadBytes)
	{
		Cycle = cycle;
		Row = row;
		PayloadBytes = payloadBytes;
		Payload = new byte[payloadBytes];

		for (var i = 0; i < Payload.Length; i += 4096)
			Payload[i] = (byte)(cycle + row + i);
	}

	public int Cycle { get; }

	public int Row { get; }

	public long PayloadBytes { get; }

	public byte[] Payload { get; }

	public string WorkOrder => $"WO-{Cycle + 1:000}-{Row + 1:000}";

	public string Customer => Customers[(Cycle + Row) % Customers.Length];

	public string Summary => $"{Customer} - {(Row % 3 == 0 ? "on-site repair" : Row % 3 == 1 ? "parts delivery" : "preventive maintenance")}";
}

internal sealed record ReproStats(
	ReproOptions Options,
	int TrackedCycles,
	int AlivePages,
	int AliveBoards,
	int AliveSwipeViews,
	int AliveSwipeItems,
	int AliveRows,
	long RetainedPayloadBytes,
	int CachedSwipeItems,
	MemorySnapshot Baseline,
	MemorySnapshot Current,
	TimeSpan Elapsed)
{
	public string ToSummary()
	{
		var expectedRows = TrackedCycles * Options.RowsPerPage;
		var expectedPayload = Options.PayloadBytesPerPage * TrackedCycles;
		var retainedPercent = expectedPayload == 0 ? 0 : RetainedPayloadBytes * 100.0 / expectedPayload;

		return string.Join(Environment.NewLine,
			$"Run: {Options.Name}",
			$"Pages pushed and popped: {TrackedCycles} in {Elapsed:mm\\:ss}",
			$"Rows created: {expectedRows}",
			$"Cached SwipeItems currently rooted: {CachedSwipeItems}",
			$"Weak refs still alive after full GC:",
			$"  pages: {AlivePages}/{TrackedCycles}",
			$"  board view models: {AliveBoards}/{TrackedCycles}",
			$"  SwipeViews: {AliveSwipeViews}/{expectedRows}",
			$"  SwipeItems: {AliveSwipeItems}/{expectedRows}",
			$"  row view models: {AliveRows}/{expectedRows}",
			$"Payload retained by alive board view models: {FormatBytes(RetainedPayloadBytes)} ({retainedPercent:0.0}% of allocated payload)",
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
