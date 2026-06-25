using System.Diagnostics;
using System.Windows.Input;

namespace ListViewRefreshCommandLeakRepro;

internal enum ReproMode
{
	CreatedPageControl,
	SharedRefreshCommand,
	SharedRefreshCommandCleared
}

internal sealed record ReproOptions(
	ReproMode Mode,
	int Cycles,
	int PayloadMegabytesPerPage,
	int DwellMilliseconds)
{
	public bool UsesSharedCommand => Mode != ReproMode.CreatedPageControl;
	public bool ClearsRefreshCommand => Mode == ReproMode.SharedRefreshCommandCleared;
	public long PayloadBytesPerPage => PayloadMegabytesPerPage * 1024L * 1024L;
	public string Name => Mode switch
	{
		ReproMode.CreatedPageControl => "control: create ListView pages without RefreshCommand",
		ReproMode.SharedRefreshCommand => "leaky shared strong ICommand RefreshCommand",
		ReproMode.SharedRefreshCommandCleared => "mitigation: clear RefreshCommand before page close",
		_ => Mode.ToString()
	};
}

internal sealed class ReproSession
{
	static readonly StrongRefreshCommand SharedRefreshCommand = new();
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

	public ListViewLeakPage CreateTrackedPage()
	{
		var payload = new LeakPayloadViewModel(CurrentCycle, Options.PayloadBytesPerPage);
		var command = Options.UsesSharedCommand ? SharedRefreshCommand : null;
		var page = new ListViewLeakPage(payload, command);

		_trackedCycles.Add(new TrackedCycle(
			CurrentCycle,
			new WeakReference(page),
			new WeakReference(page.RefreshListView),
			new WeakReference(payload),
			payload.PayloadBytes));

		if (Options.ClearsRefreshCommand)
			page.RefreshListView.RefreshCommand = null;

		return page;
	}

	public ReproStats GetStats(MemorySnapshot baseline, MemorySnapshot current)
	{
		var alivePages = 0;
		var aliveListViews = 0;
		var alivePayloads = 0;
		long retainedPayloadBytes = 0;

		foreach (var cycle in _trackedCycles)
		{
			if (cycle.Page.IsAlive)
				alivePages++;

			if (cycle.ListView.IsAlive)
				aliveListViews++;

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
			aliveListViews,
			alivePayloads,
			retainedPayloadBytes,
			baseline,
			current,
			_elapsed.Elapsed);
	}

	sealed record TrackedCycle(
		int Cycle,
		WeakReference Page,
		WeakReference ListView,
		WeakReference Payload,
		long PayloadBytes);

	sealed class StrongRefreshCommand : ICommand
	{
		public event EventHandler? CanExecuteChanged;

		public bool CanExecute(object? parameter) => true;

		public void Execute(object? parameter)
		{
		}

		public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
	}
}

internal sealed class ListViewLeakPage : ContentPage
{
	public ListViewLeakPage(LeakPayloadViewModel payload, ICommand? refreshCommand)
	{
		Title = payload.Title;
		BindingContext = payload;

		RefreshListView = new ListView
		{
			AutomationId = $"RefreshListView-{payload.Cycle}",
			BindingContext = payload,
			IsPullToRefreshEnabled = true,
			RefreshCommand = refreshCommand,
			ItemsSource = payload.Rows,
			Header = $"{payload.Title}: {payload.PayloadBytes / 1024d / 1024d:0.0} MB cached refresh payload",
			ItemTemplate = new DataTemplate(() =>
			{
				var cell = new TextCell();
				cell.SetBinding(TextCell.TextProperty, nameof(RefreshAuditRow.Id));
				cell.SetBinding(TextCell.DetailProperty, nameof(RefreshAuditRow.Summary));
				return cell;
			})
		};

		Content = RefreshListView;
	}

	public ListView RefreshListView { get; }
}

internal sealed class LeakPayloadViewModel
{
	public LeakPayloadViewModel(int cycle, long payloadBytes)
	{
		Cycle = cycle;
		PayloadBytes = payloadBytes;
		CachedRefreshBytes = new byte[payloadBytes];

		for (var i = 0; i < CachedRefreshBytes.Length; i += 4096)
			CachedRefreshBytes[i] = (byte)(cycle + i);

		Rows = Enumerable.Range(1, 50)
			.Select(index => new RefreshAuditRow(
				$"REFRESH-{cycle + 1:000}-{index:000}",
				index % 3 == 0 ? "Inventory sync" : "Customer activity refresh"))
			.ToArray();
	}

	public int Cycle { get; }

	public long PayloadBytes { get; }

	public byte[] CachedRefreshBytes { get; }

	public IReadOnlyList<RefreshAuditRow> Rows { get; }

	public string Title => $"Refresh page {Cycle + 1}";
}

internal sealed record RefreshAuditRow(string Id, string Summary);

internal sealed record ReproStats(
	ReproOptions Options,
	int TrackedCycles,
	int AlivePages,
	int AliveListViews,
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
			$"Pages created: {TrackedCycles} in {Elapsed:mm\\:ss}",
			$"Shared strong ICommand RefreshCommand: {(Options.UsesSharedCommand ? "yes" : "no")}",
			$"RefreshCommand cleared before close: {(Options.ClearsRefreshCommand ? "yes" : "no")}",
			$"Weak refs still alive after full GC:",
			$"  pages: {AlivePages}/{TrackedCycles}",
			$"  ListViews: {AliveListViews}/{TrackedCycles}",
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
