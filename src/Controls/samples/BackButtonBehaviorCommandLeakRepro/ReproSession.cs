using System.Diagnostics;
using System.Windows.Input;

namespace BackButtonBehaviorCommandLeakRepro;

internal enum ReproMode
{
	CreatedPageControl,
	SharedBackCommand,
	SharedBackCommandCleared
}

internal sealed record ReproOptions(
	ReproMode Mode,
	int Cycles,
	int PayloadMegabytesPerPage,
	int DwellMilliseconds)
{
	public bool UsesSharedCommand => Mode != ReproMode.CreatedPageControl;
	public bool ClearsCommand => Mode == ReproMode.SharedBackCommandCleared;
	public long PayloadBytesPerPage => PayloadMegabytesPerPage * 1024L * 1024L;
	public string Name => Mode switch
	{
		ReproMode.CreatedPageControl => "control: create Shell pages without BackButtonBehavior command",
		ReproMode.SharedBackCommand => "leaky shared strong ICommand BackButtonBehavior.Command",
		ReproMode.SharedBackCommandCleared => "mitigation: clear BackButtonBehavior.Command before page close",
		_ => Mode.ToString()
	};
}

internal sealed class ReproSession
{
	static readonly StrongBackCommand SharedBackCommand = new();
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

	public BackButtonLeakPage CreateTrackedPage()
	{
		var payload = new LeakPayloadViewModel(CurrentCycle, Options.PayloadBytesPerPage);
		var command = Options.UsesSharedCommand ? SharedBackCommand : null;
		var page = new BackButtonLeakPage(payload, command);

		_trackedCycles.Add(new TrackedCycle(
			CurrentCycle,
			new WeakReference(page),
			new WeakReference(page.BackBehavior),
			new WeakReference(payload),
			payload.PayloadBytes));

		if (Options.ClearsCommand)
			page.BackBehavior.Command = null;

		return page;
	}

	public ReproStats GetStats(MemorySnapshot baseline, MemorySnapshot current)
	{
		var alivePages = 0;
		var aliveBehaviors = 0;
		var alivePayloads = 0;
		long retainedPayloadBytes = 0;

		foreach (var cycle in _trackedCycles)
		{
			if (cycle.Page.IsAlive)
				alivePages++;

			if (cycle.Behavior.IsAlive)
				aliveBehaviors++;

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
			aliveBehaviors,
			alivePayloads,
			retainedPayloadBytes,
			baseline,
			current,
			_elapsed.Elapsed);
	}

	sealed record TrackedCycle(
		int Cycle,
		WeakReference Page,
		WeakReference Behavior,
		WeakReference Payload,
		long PayloadBytes);

	sealed class StrongBackCommand : ICommand
	{
		public event EventHandler? CanExecuteChanged;

		public bool CanExecute(object? parameter) => true;

		public void Execute(object? parameter)
		{
		}

		public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
	}
}

internal sealed class BackButtonLeakPage : ContentPage
{
	public BackButtonLeakPage(LeakPayloadViewModel payload, ICommand? command)
	{
		Title = payload.Title;
		BindingContext = payload;

		BackBehavior = new BackButtonBehavior
		{
			BindingContext = payload,
			Command = command,
			CommandParameter = payload,
			TextOverride = "Back"
		};

		Shell.SetBackButtonBehavior(this, BackBehavior);

		Content = new VerticalStackLayout
		{
			Padding = new Thickness(18),
			Spacing = 12,
			Children =
			{
				new Label
				{
					Text = payload.Title,
					FontSize = 22,
					FontAttributes = FontAttributes.Bold,
					TextColor = Color.FromArgb("#0B1F33")
				},
				new Label
				{
					Text = $"Back command audit payload: {payload.PayloadBytes / 1024d / 1024d:0.0} MB",
					FontSize = 14,
					TextColor = Color.FromArgb("#57606A")
				}
			}
		};
	}

	public BackButtonBehavior BackBehavior { get; }
}

internal sealed class LeakPayloadViewModel
{
	public LeakPayloadViewModel(int cycle, long payloadBytes)
	{
		Cycle = cycle;
		PayloadBytes = payloadBytes;
		CachedBackCommandBytes = new byte[payloadBytes];

		for (var i = 0; i < CachedBackCommandBytes.Length; i += 4096)
			CachedBackCommandBytes[i] = (byte)(cycle + i);

		AuditRows = Enumerable.Range(1, 40)
			.Select(index => new BackAuditRow(
				$"BACK-{cycle + 1:000}-{index:000}",
				index % 2 == 0 ? "Unsaved form guard" : "Navigation policy check"))
			.ToArray();
	}

	public int Cycle { get; }

	public long PayloadBytes { get; }

	public byte[] CachedBackCommandBytes { get; }

	public IReadOnlyList<BackAuditRow> AuditRows { get; }

	public string Title => $"Back behavior page {Cycle + 1}";
}

internal sealed record BackAuditRow(string Id, string Summary);

internal sealed record ReproStats(
	ReproOptions Options,
	int TrackedCycles,
	int AlivePages,
	int AliveBehaviors,
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
			$"Shared strong ICommand BackButtonBehavior.Command: {(Options.UsesSharedCommand ? "yes" : "no")}",
			$"BackButtonBehavior.Command cleared before close: {(Options.ClearsCommand ? "yes" : "no")}",
			$"Weak refs still alive after full GC:",
			$"  pages: {AlivePages}/{TrackedCycles}",
			$"  BackButtonBehaviors: {AliveBehaviors}/{TrackedCycles}",
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
