using System.Diagnostics;

namespace FormattedTextLeakRepro;

internal enum ReproMode
{
	SharedResourceFormattedText,
	InlineFormattedTextControl,
	ClearFormattedTextOnDisappear
}

internal sealed record ReproOptions(
	ReproMode Mode,
	int Pages,
	int DisclosuresPerPage,
	int PayloadKilobytesPerDisclosure,
	int DwellMilliseconds)
{
	public bool UseSharedFormattedText => Mode != ReproMode.InlineFormattedTextControl;
	public bool ClearFormattedTextOnDisappear => Mode == ReproMode.ClearFormattedTextOnDisappear;
	public long PayloadBytesPerDisclosure => PayloadKilobytesPerDisclosure * 1024L;
	public string Name => Mode switch
	{
		ReproMode.SharedResourceFormattedText => "leaky shared Application.Resources FormattedString",
		ReproMode.InlineFormattedTextControl => "control: inline FormattedString per label",
		ReproMode.ClearFormattedTextOnDisappear => "mitigation: clear FormattedText on disappear",
		_ => Mode.ToString()
	};
}

internal sealed class ReproSession
{
	readonly List<TrackedPage> _trackedPages = new();
	readonly Stopwatch _elapsed = Stopwatch.StartNew();
	int _currentPage = -1;

	public ReproSession(ReproOptions options)
	{
		Options = options;
	}

	public static ReproSession? Current { get; set; }

	public ReproOptions Options { get; }

	public int CurrentPage => _currentPage;

	public int BeginNextPage() => ++_currentPage;

	public void Track(ContentPage page, IReadOnlyList<Label> disclosureLabels, IReadOnlyList<DisclosureRowViewModel> rows)
	{
		_trackedPages.Add(new TrackedPage(
			CurrentPage,
			new WeakReference(page),
			disclosureLabels.Select(static label => new WeakReference(label)).ToArray(),
			rows.Select(static row => new WeakReference(row)).ToArray(),
			rows.Count == 0 ? 0 : rows[0].PayloadBytes));
	}

	public ReproStats GetStats(MemorySnapshot baseline, MemorySnapshot current)
	{
		var alivePages = 0;
		var aliveLabels = 0;
		var aliveRows = 0;
		long retainedPayloadBytes = 0;

		foreach (var page in _trackedPages)
		{
			if (page.Page.IsAlive)
				alivePages++;

			aliveLabels += page.DisclosureLabels.Count(static weakReference => weakReference.IsAlive);

			foreach (var row in page.Rows)
			{
				if (row.IsAlive)
				{
					aliveRows++;
					retainedPayloadBytes += page.PayloadBytesPerRow;
				}
			}
		}

		return new ReproStats(
			Options,
			_trackedPages.Count,
			_trackedPages.Sum(static page => page.DisclosureLabels.Count),
			alivePages,
			aliveLabels,
			aliveRows,
			retainedPayloadBytes,
			baseline,
			current,
			_elapsed.Elapsed);
	}

	sealed record TrackedPage(
		int PageNumber,
		WeakReference Page,
		IReadOnlyList<WeakReference> DisclosureLabels,
		IReadOnlyList<WeakReference> Rows,
		long PayloadBytesPerRow);
}

internal sealed class DisclosureRowViewModel
{
	public DisclosureRowViewModel(int page, int row, long payloadBytes)
	{
		Page = page;
		Row = row;
		PayloadBytes = payloadBytes;
		Payload = new byte[payloadBytes];

		for (var i = 0; i < Payload.Length; i += 4096)
			Payload[i] = (byte)(page + row + i);
	}

	public int Page { get; }

	public int Row { get; }

	public long PayloadBytes { get; }

	public byte[] Payload { get; }

	public string CustomerName => $"Customer {Page + 1:00}-{Row + 1:00}";

	public string OrderNumber => $"ORD-{DateTime.Today:yyyyMMdd}-{Page + 1:000}-{Row + 1:000}";

	public string Title => $"{CustomerName} - {OrderNumber}";
}

internal sealed record ReproStats(
	ReproOptions Options,
	int TrackedPages,
	int TrackedDisclosures,
	int AlivePages,
	int AliveDisclosureLabels,
	int AliveRows,
	long RetainedPayloadBytes,
	MemorySnapshot Baseline,
	MemorySnapshot Current,
	TimeSpan Elapsed)
{
	public string ToSummary()
	{
		var expectedPayload = Options.PayloadBytesPerDisclosure * TrackedDisclosures;
		var retainedPercent = expectedPayload == 0 ? 0 : RetainedPayloadBytes * 100.0 / expectedPayload;

		return string.Join(Environment.NewLine,
			$"Run: {Options.Name}",
			$"Pages pushed and popped: {TrackedPages} in {Elapsed:mm\\:ss}",
			$"Disclosures created: {TrackedDisclosures}",
			$"Weak refs still alive after full GC:",
			$"  pages: {AlivePages}/{TrackedPages}",
			$"  disclosure labels: {AliveDisclosureLabels}/{TrackedDisclosures}",
			$"  row view models: {AliveRows}/{TrackedDisclosures}",
			$"Payload retained by alive row view models: {FormatBytes(RetainedPayloadBytes)} ({retainedPercent:0.0}% of allocated payload)",
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
