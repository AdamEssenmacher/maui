using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows.Input;

namespace BorderDashArrayLeakRepro;

internal enum ReproMode
{
	SharedAppResourceDashArray,
	SolidBorderControl,
	PerBorderDashArrayMitigation
}

internal sealed record ReproOptions(
	ReproMode Mode,
	int Pages,
	int CardsPerPage,
	int ItemPayloadKilobytes,
	int PagePayloadMegabytes,
	int DwellMilliseconds)
{
	public bool UseSharedDashArray => Mode == ReproMode.SharedAppResourceDashArray;
	public bool UsePerBorderDashArray => Mode == ReproMode.PerBorderDashArrayMitigation;
	public long ItemPayloadBytes => ItemPayloadKilobytes * 1024L;
	public long PagePayloadBytes => PagePayloadMegabytes * 1024L * 1024L;
	public long PayloadBytesPerPage => PagePayloadBytes + CardsPerPage * ItemPayloadBytes;
	public string Name => Mode switch
	{
		ReproMode.SharedAppResourceDashArray => "leaky shared AppResource StrokeDashArray",
		ReproMode.SolidBorderControl => "control: solid borders",
		ReproMode.PerBorderDashArrayMitigation => "mitigation: per-border dash arrays",
		_ => Mode.ToString()
	};
}

internal sealed class ReproSession
{
	readonly List<TrackedPage> _trackedPages = new();
	readonly List<TrackedCard> _trackedCards = new();
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

	public void TrackPage(ContentPage page, CollectionView collectionView, PagePayloadViewModel payload)
	{
		_trackedPages.Add(new TrackedPage(
			CurrentCycle,
			new WeakReference(page),
			new WeakReference(collectionView),
			new WeakReference(payload),
			payload.PayloadBytes));

		foreach (var card in payload.Cards)
			TrackCardPayload(card);
	}

	public void TrackCardBorder(Border border, CardPayloadViewModel payload)
	{
		_trackedCards.Add(new TrackedCard(
			new WeakReference(border),
			new WeakReference(payload),
			payload.PayloadBytes,
			true));
	}

	void TrackCardPayload(CardPayloadViewModel payload)
	{
		_trackedCards.Add(new TrackedCard(
			null,
			new WeakReference(payload),
			payload.PayloadBytes,
			false));
	}

	public ReproStats GetStats(MemorySnapshot baseline, MemorySnapshot current)
	{
		var alivePages = 0;
		var aliveCollectionViews = 0;
		var alivePageViewModels = 0;
		var aliveCardViewModels = 0;
		var aliveCardBorders = 0;
		var trackedCardBorders = 0;
		long retainedPayloadBytes = 0;

		foreach (var page in _trackedPages)
		{
			if (page.Page.IsAlive)
				alivePages++;

			if (page.CollectionView.IsAlive)
				aliveCollectionViews++;

			if (page.ViewModel.IsAlive)
				alivePageViewModels++;
		}

		foreach (var card in _trackedCards)
		{
			if (card.TracksBorder)
			{
				trackedCardBorders++;

				if (card.Border?.IsAlive == true)
					aliveCardBorders++;
			}
			else if (card.Payload.IsAlive)
			{
				aliveCardViewModels++;
			}
		}

		retainedPayloadBytes = alivePages * Options.PayloadBytesPerPage;

		return new ReproStats(
			Options,
			_trackedPages.Count,
			trackedCardBorders,
			alivePages,
			aliveCollectionViews,
			alivePageViewModels,
			aliveCardViewModels,
			aliveCardBorders,
			retainedPayloadBytes,
			baseline,
			current,
			_elapsed.Elapsed);
	}

	sealed record TrackedPage(
		int Cycle,
		WeakReference Page,
		WeakReference CollectionView,
		WeakReference ViewModel,
		long PayloadBytes);

	sealed record TrackedCard(
		WeakReference? Border,
		WeakReference Payload,
		long PayloadBytes,
		bool TracksBorder);
}

internal sealed class PagePayloadViewModel
{
	public PagePayloadViewModel(int cycle, ReproOptions options)
	{
		Cycle = cycle;
		PayloadBytes = options.PagePayloadBytes;
		Payload = CreatePayload(PayloadBytes, cycle);
		OpenCardCommand = new Command<CardPayloadViewModel>(_ => Taps++);

		for (var i = 0; i < options.CardsPerPage; i++)
			Cards.Add(new CardPayloadViewModel(cycle, i, options.ItemPayloadBytes));
	}

	public int Cycle { get; }

	public long PayloadBytes { get; }

	public byte[] Payload { get; }

	public ObservableCollection<CardPayloadViewModel> Cards { get; } = new();

	public ICommand OpenCardCommand { get; }

	public int Taps { get; private set; }

	public string Title => $"Customer portfolio {Cycle + 1}";

	static byte[] CreatePayload(long payloadBytes, int salt)
	{
		var payload = new byte[payloadBytes];

		for (var i = 0; i < payload.Length; i += 4096)
			payload[i] = (byte)(salt + i);

		return payload;
	}
}

internal sealed class CardPayloadViewModel
{
	public CardPayloadViewModel(int pageCycle, int index, long payloadBytes)
	{
		PageCycle = pageCycle;
		Index = index;
		PayloadBytes = payloadBytes;
		Payload = CreatePayload(payloadBytes, pageCycle, index);
		Status = Statuses[(pageCycle + index) % Statuses.Length];
		Owner = Owners[(pageCycle * 7 + index) % Owners.Length];
		Amount = 25000 + (pageCycle * 8191 + index * 977) % 950000;
	}

	static readonly string[] Statuses = ["Pending review", "Escalated", "Awaiting signature", "In underwriting"];
	static readonly string[] Owners = ["Avery Stone", "Jordan Lee", "Sam Rivera", "Morgan Patel", "Taylor Kim"];

	public int PageCycle { get; }

	public int Index { get; }

	public long PayloadBytes { get; }

	public byte[] Payload { get; }

	public string Title => $"Account {PageCycle + 1:00}-{Index + 1:000}";

	public string Status { get; }

	public string Owner { get; }

	public int Amount { get; }

	public string AmountText => $"${Amount:N0}";

	static byte[] CreatePayload(long payloadBytes, int pageCycle, int index)
	{
		var payload = new byte[payloadBytes];

		for (var i = 0; i < payload.Length; i += 4096)
			payload[i] = (byte)(pageCycle + index + i);

		return payload;
	}
}

internal sealed record ReproStats(
	ReproOptions Options,
	int TrackedPages,
	int TrackedCardBorders,
	int AlivePages,
	int AliveCollectionViews,
	int AlivePageViewModels,
	int AliveCardViewModels,
	int AliveCardBorders,
	long RetainedPayloadBytes,
	MemorySnapshot Baseline,
	MemorySnapshot Current,
	TimeSpan Elapsed)
{
	public string ToSummary()
	{
		var expectedPayload = Options.PayloadBytesPerPage * TrackedPages;
		var retainedPercent = expectedPayload == 0 ? 0 : RetainedPayloadBytes * 100.0 / expectedPayload;

		return string.Join(Environment.NewLine,
			$"Run: {Options.Name}",
			$"Pages pushed and popped: {TrackedPages} in {Elapsed:mm\\:ss}",
			$"Weak refs still alive after full GC:",
			$"  pages: {AlivePages}/{TrackedPages}",
			$"  CollectionViews: {AliveCollectionViews}/{TrackedPages}",
			$"  page view models: {AlivePageViewModels}/{TrackedPages} (reported as a raw weak-ref signal only)",
			$"  card view models: {AliveCardViewModels}/{TrackedPages * Options.CardsPerPage} (reported as a raw weak-ref signal only)",
			$"  realized card Borders: {AliveCardBorders}/{TrackedCardBorders}",
			$"Payload definitely retained through alive pages: {FormatBytes(RetainedPayloadBytes)} ({retainedPercent:0.0}% of allocated payload)",
			$"Managed heap delta after GC: {FormatBytes(Current.ManagedBytes - Baseline.ManagedBytes)}",
			$"GC heap delta after GC: {FormatBytes(Current.GcHeapBytes - Baseline.GcHeapBytes)}",
			$"Resident memory delta: {FormatBytes(Current.ResidentBytes - Baseline.ResidentBytes)}",
			$"Working set delta: {FormatBytes(Current.WorkingSetBytes - Baseline.WorkingSetBytes)}");
	}

	public static string FormatBytes(long bytes)
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
