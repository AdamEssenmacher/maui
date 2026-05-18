using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;

namespace SelectedItemsLeakRepro;

internal enum ReproMode
{
	ObservableSelection,
	RetainedListControl,
	PageScopedObservableControl
}

internal sealed record ReproOptions(
	ReproMode Mode,
	int Cycles,
	int RowsPerPage,
	int SelectedItemsPerPage,
	int PayloadMegabytesPerPage,
	int DwellMilliseconds)
{
	public bool UseObservableSelection => Mode != ReproMode.RetainedListControl;

	public bool RetainSelectionState => Mode != ReproMode.PageScopedObservableControl;

	public int PayloadBytesPerPage => PayloadMegabytesPerPage * 1024 * 1024;

	public string Name => Mode switch
	{
		ReproMode.ObservableSelection => "Leaky retained ObservableCollection selected state",
		ReproMode.RetainedListControl => "Control: retained List selected state",
		_ => "Control: page-scoped ObservableCollection selected state"
	};

	public string SelectionStateKind => UseObservableSelection ? "ObservableCollection<object>" : "List<object>";
}

internal static class SelectionStateStore
{
	static readonly Dictionary<int, IList<object>> s_retainedSelections = new();

	public static int Count => s_retainedSelections.Count;

	public static void Reset()
	{
		s_retainedSelections.Clear();
	}

	public static IList<object> CreateSelection(int cycle, IReadOnlyList<CustomerRecord> customers, int selectedCount, ReproOptions options)
	{
		IList<object> selected = options.UseObservableSelection
			? new ObservableCollection<object>()
			: new List<object>();

		var count = Math.Min(selectedCount, customers.Count);

		for (var i = 0; i < count; i++)
		{
			var index = (cycle * 37 + i * 13) % customers.Count;
			selected.Add(customers[index]);
		}

		if (options.RetainSelectionState)
			s_retainedSelections[cycle] = selected;

		return selected;
	}
}

internal sealed class ReproSession
{
	readonly List<PageCapture> _captures = new();
	readonly DateTimeOffset _started = DateTimeOffset.Now;

	public ReproSession(ReproOptions options)
	{
		Options = options;
	}

	public static ReproSession? Current { get; set; }

	public ReproOptions Options { get; }

	public int CurrentCycle { get; private set; }

	public void BeginNextCycle()
	{
		CurrentCycle++;
	}

	public void Track(
		ContentPage page,
		CollectionView collectionView,
		CustomerSelectionViewModel viewModel,
		object selectedItemsWrapper,
		IList<object> selectedState)
	{
		_captures.Add(new PageCapture(
			new WeakReference(page),
			new WeakReference(collectionView),
			new WeakReference(viewModel),
			new WeakReference(selectedItemsWrapper),
			new WeakReference(selectedState),
			viewModel.PayloadBytes));
	}

	public ReproStats GetStats(MemorySnapshot baseline, MemorySnapshot current)
	{
		var alivePages = 0;
		var aliveCollectionViews = 0;
		var aliveViewModels = 0;
		var aliveSelectionLists = 0;
		var aliveSelectionStates = 0;
		long retainedPayloadBytes = 0;

		foreach (var capture in _captures)
		{
			if (capture.Page.IsAlive)
				alivePages++;

			if (capture.CollectionView.IsAlive)
				aliveCollectionViews++;

			if (capture.ViewModel.IsAlive)
			{
				aliveViewModels++;
				retainedPayloadBytes += capture.PayloadBytes;
			}

			if (capture.SelectionList.IsAlive)
				aliveSelectionLists++;

			if (capture.SelectedState.IsAlive)
				aliveSelectionStates++;
		}

		return new ReproStats(
			Options,
			_captures.Count,
			alivePages,
			aliveCollectionViews,
			aliveViewModels,
			aliveSelectionLists,
			aliveSelectionStates,
			SelectionStateStore.Count,
			retainedPayloadBytes,
			Options.PayloadBytesPerPage * (long)_captures.Count,
			current.ManagedBytes - baseline.ManagedBytes,
			current.GcHeapBytes - baseline.GcHeapBytes,
			current.ResidentBytes - baseline.ResidentBytes,
			current.WorkingSetBytes - baseline.WorkingSetBytes,
			DateTimeOffset.Now - _started);
	}

	sealed record PageCapture(
		WeakReference Page,
		WeakReference CollectionView,
		WeakReference ViewModel,
		WeakReference SelectionList,
		WeakReference SelectedState,
		long PayloadBytes);
}

internal sealed record ReproStats(
	ReproOptions Options,
	int TrackedCycles,
	int AlivePages,
	int AliveCollectionViews,
	int AliveViewModels,
	int AliveSelectionLists,
	int AliveSelectionStates,
	int RetainedSelectionStates,
	long RetainedPayloadBytes,
	long AllocatedPayloadBytes,
	long ManagedDeltaBytes,
	long GcHeapDeltaBytes,
	long ResidentDeltaBytes,
	long WorkingSetDeltaBytes,
	TimeSpan Elapsed)
{
	public string ToSummary()
	{
		var builder = new StringBuilder();
		var retainedPercent = AllocatedPayloadBytes == 0 ? 0 : RetainedPayloadBytes / (double)AllocatedPayloadBytes;

		builder.AppendLine(Options.Name);
		builder.AppendLine(CultureInfo.InvariantCulture, $"Pages pushed and popped: {TrackedCycles} in {Elapsed:mm\\:ss}");
		builder.AppendLine(CultureInfo.InvariantCulture, $"Selection state: {Options.SelectionStateKind}; retained store entries: {RetainedSelectionStates}");
		builder.AppendLine();
		builder.AppendLine("Weak references alive after full GC:");
		builder.AppendLine(CultureInfo.InvariantCulture, $"  Pages: {AlivePages} / {TrackedCycles}");
		builder.AppendLine(CultureInfo.InvariantCulture, $"  CollectionViews: {AliveCollectionViews} / {TrackedCycles}");
		builder.AppendLine(CultureInfo.InvariantCulture, $"  Page view models: {AliveViewModels} / {TrackedCycles}");
		builder.AppendLine(CultureInfo.InvariantCulture, $"  SelectionList wrappers: {AliveSelectionLists} / {TrackedCycles}");
		builder.AppendLine(CultureInfo.InvariantCulture, $"  Selected state collections: {AliveSelectionStates} / {TrackedCycles}");
		builder.AppendLine();
		builder.AppendLine(CultureInfo.InvariantCulture, $"Page payload retained by alive view models: {FormatBytes(RetainedPayloadBytes)} of {FormatBytes(AllocatedPayloadBytes)} ({retainedPercent:P0})");
		builder.AppendLine(CultureInfo.InvariantCulture, $"Managed memory delta: {FormatBytes(ManagedDeltaBytes)}");
		builder.AppendLine(CultureInfo.InvariantCulture, $"GC heap delta: {FormatBytes(GcHeapDeltaBytes)}");
		builder.AppendLine(CultureInfo.InvariantCulture, $"Resident memory delta: {FormatBytes(ResidentDeltaBytes)}");
		builder.AppendLine(CultureInfo.InvariantCulture, $"Working set delta: {FormatBytes(WorkingSetDeltaBytes)}");

		return builder.ToString();
	}

	public static string FormatBytes(long bytes)
	{
		var sign = bytes < 0 ? "-" : string.Empty;
		var value = Math.Abs((double)bytes);
		string[] units = ["B", "KB", "MB", "GB"];
		var unit = 0;

		while (value >= 1024 && unit < units.Length - 1)
		{
			value /= 1024;
			unit++;
		}

		return string.Create(CultureInfo.InvariantCulture, $"{sign}{value:0.0} {units[unit]}");
	}
}

internal sealed class CustomerSelectionViewModel
{
	public CustomerSelectionViewModel(
		int cycle,
		IReadOnlyList<CustomerRecord> customers,
		IList<object> selectedCustomers,
		int payloadBytes)
	{
		Title = string.Create(CultureInfo.InvariantCulture, $"Renewal batch {cycle:000}");
		Customers = customers;
		SelectedCustomers = selectedCustomers;
		PagePayload = GC.AllocateUninitializedArray<byte>(payloadBytes);

		for (var i = 0; i < PagePayload.Length; i += 4096)
			PagePayload[i] = (byte)((cycle + i) % 251);
	}

	public string Title { get; }

	public IReadOnlyList<CustomerRecord> Customers { get; }

	public IList<object> SelectedCustomers { get; }

	public byte[] PagePayload { get; }

	public long PayloadBytes => PagePayload.LongLength;
}

internal sealed record CustomerRecord(
	int Id,
	string Name,
	string Company,
	string Region,
	string Status,
	string RenewalWindow,
	decimal AnnualValue,
	int RiskScore)
{
	public string DisplayName => string.Create(CultureInfo.InvariantCulture, $"{Name} - {Company}");

	public string Detail => string.Create(CultureInfo.InvariantCulture, $"{Region} / {RenewalWindow} / risk {RiskScore}");

	public string ValueLabel => AnnualValue.ToString("C0", CultureInfo.CurrentCulture);
}

internal static class CustomerFactory
{
	static readonly string[] s_firstNames =
	[
		"Alex", "Briana", "Casey", "Devon", "Elliot", "Fatima", "Gabe", "Harper",
		"Indira", "Jules", "Kai", "Lina", "Morgan", "Nadia", "Owen", "Priya"
	];

	static readonly string[] s_lastNames =
	[
		"Rivera", "Chen", "Patel", "Williams", "Nguyen", "Garcia", "Brown", "Singh",
		"Johnson", "Kim", "Miller", "Davis", "Wilson", "Martin", "Taylor", "Clark"
	];

	static readonly string[] s_companies =
	[
		"Contoso Retail", "Fabrikam Health", "Northwind Logistics", "Tailspin Energy",
		"Woodgrove Bank", "Blue Yonder Labs", "Adventure Works", "Proseware Media"
	];

	static readonly string[] s_regions =
	[
		"North America", "EMEA", "Latin America", "APAC", "US Public Sector"
	];

	static readonly string[] s_statuses =
	[
		"Ready", "Needs legal", "Discount review", "Executive sponsor", "Expansion"
	];

	public static IReadOnlyList<CustomerRecord> Create(int cycle, int count)
	{
		var customers = new List<CustomerRecord>(count);

		for (var i = 0; i < count; i++)
		{
			var id = cycle * 100000 + i;
			var name = string.Create(CultureInfo.InvariantCulture, $"{s_firstNames[(i + cycle) % s_firstNames.Length]} {s_lastNames[(i * 3 + cycle) % s_lastNames.Length]}");
			var company = s_companies[(i + cycle * 2) % s_companies.Length];
			var region = s_regions[(i * 5 + cycle) % s_regions.Length];
			var status = s_statuses[(i * 7 + cycle) % s_statuses.Length];
			var renewalWindow = string.Create(CultureInfo.InvariantCulture, $"Q{(i + cycle) % 4 + 1} FY{2026 + (i % 3)}");
			var annualValue = 25000m + ((i * 7919 + cycle * 6151) % 975000);
			var riskScore = 20 + ((i * 11 + cycle * 17) % 80);

			customers.Add(new CustomerRecord(id, name, company, region, status, renewalWindow, annualValue, riskScore));
		}

		return customers;
	}
}
