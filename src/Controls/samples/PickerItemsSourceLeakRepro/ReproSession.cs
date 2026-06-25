using System.Collections.ObjectModel;
using System.Diagnostics;

namespace PickerItemsSourceLeakRepro;

internal enum ReproMode
{
	SharedItemsSource,
	FreshItemsSourceControl,
	ClearSharedItemsSourceOnDisappear
}

internal sealed record ReproOptions(
	ReproMode Mode,
	int Cycles,
	int PickersPerPage,
	int ChoicesPerPicker,
	int PayloadMegabytesPerPage,
	int DwellMilliseconds)
{
	public bool UsesSharedItemsSource => Mode != ReproMode.FreshItemsSourceControl;
	public bool ClearItemsSourceOnDisappear => Mode == ReproMode.ClearSharedItemsSourceOnDisappear;
	public long PayloadBytesPerPage => PayloadMegabytesPerPage * 1024L * 1024L;
	public string Name => Mode switch
	{
		ReproMode.SharedItemsSource => "leaky shared ObservableCollection ItemsSource",
		ReproMode.FreshItemsSourceControl => "control: fresh ObservableCollection per Picker",
		ReproMode.ClearSharedItemsSourceOnDisappear => "mitigation: clear shared Picker.ItemsSource",
		_ => Mode.ToString()
	};
}

internal sealed class ReproSession
{
	readonly List<TrackedCycle> _trackedCycles = new();
	readonly Stopwatch _elapsed = Stopwatch.StartNew();
	readonly ObservableCollection<string>? _sharedChoices;
	int _currentCycle = -1;

	public ReproSession(ReproOptions options)
	{
		Options = options;

		if (options.UsesSharedItemsSource)
			_sharedChoices = CreateChoices(options.ChoicesPerPicker);
	}

	public static ReproSession? Current { get; set; }

	public ReproOptions Options { get; }

	public int CurrentCycle => _currentCycle;

	public int BeginNextCycle() => ++_currentCycle;

	public ObservableCollection<string> CreateItemsSource()
	{
		return _sharedChoices ?? CreateChoices(Options.ChoicesPerPicker);
	}

	public void Track(ContentPage page, IReadOnlyList<Picker> pickers, LeakPayloadViewModel payload)
	{
		_trackedCycles.Add(new TrackedCycle(
			CurrentCycle,
			new WeakReference(page),
			pickers.Select(picker => new WeakReference(picker)).ToArray(),
			new WeakReference(payload),
			payload.PayloadBytes));
	}

	public ReproStats GetStats(MemorySnapshot baseline, MemorySnapshot current)
	{
		var alivePages = 0;
		var alivePickers = 0;
		var totalPickers = 0;
		var alivePayloads = 0;
		long retainedPayloadBytes = 0;

		foreach (var cycle in _trackedCycles)
		{
			if (cycle.Page.IsAlive)
				alivePages++;

			foreach (var picker in cycle.Pickers)
			{
				totalPickers++;

				if (picker.IsAlive)
					alivePickers++;
			}

			if (cycle.Payload.IsAlive)
			{
				alivePayloads++;
				retainedPayloadBytes += cycle.PayloadBytes;
			}
		}

		return new ReproStats(
			Options,
			_trackedCycles.Count,
			totalPickers,
			alivePages,
			alivePickers,
			alivePayloads,
			retainedPayloadBytes,
			baseline,
			current,
			_elapsed.Elapsed);
	}

	static ObservableCollection<string> CreateChoices(int count)
	{
		return new ObservableCollection<string>(
			Enumerable.Range(1, count)
				.Select(index => $"Warehouse region {index:000}"));
	}

	sealed record TrackedCycle(
		int Cycle,
		WeakReference Page,
		IReadOnlyList<WeakReference> Pickers,
		WeakReference Payload,
		long PayloadBytes);
}

internal sealed class LeakPayloadViewModel
{
	public LeakPayloadViewModel(int cycle, long payloadBytes, int pickerCount)
	{
		Cycle = cycle;
		PayloadBytes = payloadBytes;
		CachedOrderBytes = new byte[payloadBytes];

		for (var i = 0; i < CachedOrderBytes.Length; i += 4096)
			CachedOrderBytes[i] = (byte)(cycle + i);

		Orders = Enumerable.Range(1, pickerCount * 10)
			.Select(index => new OrderRow(
				$"ORD-{cycle + 1:000}-{index:000}",
				$"Customer shipment {index}",
				"Awaiting routing"))
			.ToArray();
	}

	public int Cycle { get; }

	public long PayloadBytes { get; }

	public byte[] CachedOrderBytes { get; }

	public IReadOnlyList<OrderRow> Orders { get; }

	public string Title => $"Fulfillment form {Cycle + 1}";
}

internal sealed record OrderRow(string Id, string Summary, string Status);

internal sealed record ReproStats(
	ReproOptions Options,
	int TrackedCycles,
	int TotalPickers,
	int AlivePages,
	int AlivePickers,
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
			$"Pickers created: {TotalPickers}",
			$"Shared ItemsSource: {(Options.UsesSharedItemsSource ? "yes" : "no")}",
			$"Weak refs still alive after full GC:",
			$"  pages: {AlivePages}/{TrackedCycles}",
			$"  Pickers: {AlivePickers}/{TotalPickers}",
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
