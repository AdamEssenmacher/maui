using System.Collections.ObjectModel;
using System.Windows.Input;

namespace SwipeItemViewCommandLeakRepro;

public enum ReproMode
{
	SwipeItemViewCommand,
	PlainSwipeItemControl,
	ClearCommandOnDisappear
}

public sealed record ReproOptions(
	ReproMode Mode,
	int Cycles,
	int RowsPerPage,
	int PayloadKilobytesPerRow,
	int DwellMilliseconds)
{
	public bool UseSwipeItemView => Mode is not ReproMode.PlainSwipeItemControl;

	public bool ClearCommandOnDisappear => Mode is ReproMode.ClearCommandOnDisappear;

	public long PayloadBytesPerRow => PayloadKilobytesPerRow * 1024L;

	public string Name => Mode switch
	{
		ReproMode.SwipeItemViewCommand => "Leaky SwipeItemView.Command",
		ReproMode.PlainSwipeItemControl => "Control: plain SwipeItem.Command",
		ReproMode.ClearCommandOnDisappear => "Mitigation: clear SwipeItemView.Command",
		_ => Mode.ToString()
	};
}

public sealed class ReproSession
{
	readonly List<WeakReference> _pages = [];
	readonly List<WeakReference> _swipeViews = [];
	readonly List<WeakReference> _actionElements = [];
	readonly List<WeakReference> _actionContentViews = [];
	readonly List<WeakReference> _rows = [];
	readonly List<WeakReference<byte[]>> _payloads = [];

	int _cycle;

	public ReproSession(ReproOptions options)
	{
		Options = options;
		MemorySampler.ForceFullCollection();
		Baseline = MemorySampler.Capture();
	}

	public static ReproSession? Current { get; set; }

	public ReproOptions Options { get; }

	public CountingCommand SharedCommand { get; } = new();

	public MemorySnapshot Baseline { get; }

	public int CurrentCycle => _cycle;

	public int TotalRowsCreated => _rows.Count;

	public long TotalPayloadBytesCreated => _payloads.Count * Options.PayloadBytesPerRow;

	public int BeginNextCycle() => ++_cycle;

	public ReadOnlyCollection<DispatchRowViewModel> CreateRowsForCurrentCycle()
	{
		var rows = new List<DispatchRowViewModel>(Options.RowsPerPage);

		for (var index = 0; index < Options.RowsPerPage; index++)
		{
			var row = new DispatchRowViewModel(_cycle, index, Options.PayloadKilobytesPerRow);
			_rows.Add(new WeakReference(row));
			_payloads.Add(new WeakReference<byte[]>(row.Payload));
			rows.Add(row);
		}

		return rows.AsReadOnly();
	}

	public void Track(
		Page page,
		IEnumerable<SwipeView> swipeViews,
		IEnumerable<Element> actionElements,
		IEnumerable<View> actionContentViews)
	{
		_pages.Add(new WeakReference(page));

		foreach (var swipeView in swipeViews)
			_swipeViews.Add(new WeakReference(swipeView));

		foreach (var actionElement in actionElements)
			_actionElements.Add(new WeakReference(actionElement));

		foreach (var actionContentView in actionContentViews)
			_actionContentViews.Add(new WeakReference(actionContentView));
	}

	public ReproStats CaptureStats(string label)
	{
		var snapshot = MemorySampler.Capture();
		var retainedPayloadBytes = CountAlivePayloads() * Options.PayloadBytesPerRow;

		return new ReproStats(
			Label: label,
			HeapBytes: snapshot.ManagedBytes,
			HeapDeltaBytes: snapshot.ManagedBytes - Baseline.ManagedBytes,
			AlivePages: CountAlive(_pages),
			AliveSwipeViews: CountAlive(_swipeViews),
			AliveActionElements: CountAlive(_actionElements),
			AliveActionContentViews: CountAlive(_actionContentViews),
			AliveRows: CountAlive(_rows),
			RetainedPayloadBytes: retainedPayloadBytes,
			CommandSubscribers: SharedCommand.SubscriberCount,
			TotalRowsCreated: TotalRowsCreated,
			TotalPayloadBytesCreated: TotalPayloadBytesCreated);
	}

	static int CountAlive(List<WeakReference> references)
	{
		var count = 0;

		foreach (var reference in references)
		{
			if (reference.IsAlive)
				count++;
		}

		return count;
	}

	int CountAlivePayloads()
	{
		var count = 0;

		foreach (var reference in _payloads)
		{
			if (reference.TryGetTarget(out _))
				count++;
		}

		return count;
	}
}

public sealed class CountingCommand : ICommand
{
	EventHandler? _canExecuteChanged;

	public int Executions { get; private set; }

	public int SubscriberCount => _canExecuteChanged?.GetInvocationList().Length ?? 0;

	public event EventHandler? CanExecuteChanged
	{
		add => _canExecuteChanged += value;
		remove => _canExecuteChanged -= value;
	}

	public bool CanExecute(object? parameter) => true;

	public void Execute(object? parameter)
	{
		Executions++;
	}

	public void RaiseCanExecuteChanged()
	{
		_canExecuteChanged?.Invoke(this, EventArgs.Empty);
	}
}

public sealed class DispatchRowViewModel
{
	public DispatchRowViewModel(int pageNumber, int rowNumber, int payloadKilobytes)
	{
		PageNumber = pageNumber;
		RowNumber = rowNumber + 1;
		Id = $"DSP-{pageNumber:000}-{RowNumber:000}";
		Customer = Customers[rowNumber % Customers.Length];
		Region = Regions[(pageNumber + rowNumber) % Regions.Length];
		Status = Statuses[(pageNumber + rowNumber) % Statuses.Length];
		Amount = 240m + ((pageNumber * 31 + rowNumber * 17) % 2200);
		Payload = new byte[payloadKilobytes * 1024];
		Array.Fill(Payload, unchecked((byte)(pageNumber + rowNumber)));
	}

	public int PageNumber { get; }

	public int RowNumber { get; }

	public string Id { get; }

	public string Customer { get; }

	public string Region { get; }

	public string Status { get; }

	public decimal Amount { get; }

	public byte[] Payload { get; }

	public string Title => $"{Customer} - {Id}";

	public string Subtitle => $"{Region} route - {Status} - {Payload.Length / 1024:N0} KB row payload";

	public string AmountText => $"${Amount:N0}";

	static readonly string[] Customers =
	[
		"Northwind Medical",
		"Contoso Retail",
		"Fabrikam Parts",
		"Adventure Works",
		"Tailspin Logistics"
	];

	static readonly string[] Regions =
	[
		"Northeast",
		"Southeast",
		"Midwest",
		"Mountain",
		"Pacific"
	];

	static readonly string[] Statuses =
	[
		"Awaiting dispatch",
		"Ready for pickup",
		"Delayed",
		"Priority",
		"Needs review"
	];
}

public sealed record ReproStats(
	string Label,
	long HeapBytes,
	long HeapDeltaBytes,
	int AlivePages,
	int AliveSwipeViews,
	int AliveActionElements,
	int AliveActionContentViews,
	int AliveRows,
	long RetainedPayloadBytes,
	int CommandSubscribers,
	int TotalRowsCreated,
	long TotalPayloadBytesCreated)
{
	public string ToDisplayString()
	{
		return
			$"{Label}\n" +
			$"Managed heap delta: {FormatBytes(HeapDeltaBytes)}\n" +
			$"Command subscribers: {CommandSubscribers:N0}\n" +
			$"Alive pages: {AlivePages:N0}\n" +
			$"Alive SwipeViews: {AliveSwipeViews:N0}\n" +
			$"Alive swipe action elements: {AliveActionElements:N0}\n" +
			$"Alive swipe action content views: {AliveActionContentViews:N0}\n" +
			$"Alive row view models: {AliveRows:N0}\n" +
			$"Retained row payload: {FormatBytes(RetainedPayloadBytes)}\n" +
			$"Rows created: {TotalRowsCreated:N0}\n" +
			$"Payload allocated by scenario: {FormatBytes(TotalPayloadBytesCreated)}";
	}

	public static string FormatBytes(long bytes)
	{
		var sign = bytes < 0 ? "-" : string.Empty;
		var absolute = Math.Abs((double)bytes);
		string[] units = ["B", "KB", "MB", "GB"];
		var unit = 0;

		while (absolute >= 1024 && unit < units.Length - 1)
		{
			absolute /= 1024;
			unit++;
		}

		return $"{sign}{absolute:N1} {units[unit]}";
	}
}
