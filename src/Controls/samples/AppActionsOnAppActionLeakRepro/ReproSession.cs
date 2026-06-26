using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Hosting;

namespace AppActionsOnAppActionLeakRepro;

internal enum ReproMode
{
	NoAppActionControl,
	ConfigureEssentialsOnAppAction
}

internal sealed record ReproOptions(
	ReproMode Mode,
	int Cycles,
	int PayloadMegabytesPerApp,
	int DwellMilliseconds)
{
	public bool UsesConfigureEssentialsOnAppAction => Mode == ReproMode.ConfigureEssentialsOnAppAction;
	public long PayloadBytesPerApp => PayloadMegabytesPerApp * 1024L * 1024L;
	public string Name => Mode switch
	{
		ReproMode.NoAppActionControl => "control: throwaway MauiApp without AppActions handler",
		ReproMode.ConfigureEssentialsOnAppAction => "leaky ConfigureEssentials OnAppAction handler",
		_ => Mode.ToString()
	};
}

internal sealed class ReproSession
{
	readonly List<TrackedCycle> _trackedCycles = new();
	readonly DateTimeOffset _started = DateTimeOffset.Now;

	public ReproSession(ReproOptions options)
	{
		Options = options;
	}

	public ReproOptions Options { get; }

	public void RunCycle(int cycle)
	{
		var payload = new LeakPayload(cycle, Options.PayloadBytesPerApp);
		var builder = MauiApp.CreateBuilder();

		if (Options.UsesConfigureEssentialsOnAppAction)
		{
			builder.ConfigureEssentials(essentials =>
			{
				essentials.OnAppAction(payload.HandleAppAction);
			});
		}

		var app = builder.Build();

		_trackedCycles.Add(new TrackedCycle(
			cycle,
			new WeakReference(app),
			new WeakReference(payload),
			payload.PayloadBytes));

		app.Dispose();
	}

	public ReproStats GetStats(MemorySnapshot baseline, MemorySnapshot current)
	{
		var aliveApps = 0;
		var alivePayloads = 0;
		long retainedPayloadBytes = 0;

		foreach (var cycle in _trackedCycles)
		{
			if (cycle.MauiApp.IsAlive)
				aliveApps++;

			if (cycle.Payload.IsAlive)
			{
				alivePayloads++;
				retainedPayloadBytes += cycle.PayloadBytes;
			}
		}

		return new ReproStats(
			Options,
			_trackedCycles.Count,
			aliveApps,
			alivePayloads,
			retainedPayloadBytes,
			baseline,
			current,
			DateTimeOffset.Now - _started);
	}

	sealed record TrackedCycle(
		int Cycle,
		WeakReference MauiApp,
		WeakReference Payload,
		long PayloadBytes);
}

internal sealed class LeakPayload
{
	readonly byte[] _cachedHostState;
	readonly IReadOnlyList<HostShortcutAuditRow> _auditRows;

	public LeakPayload(int cycle, long payloadBytes)
	{
		Cycle = cycle;
		PayloadBytes = payloadBytes;
		_cachedHostState = new byte[payloadBytes];

		for (var i = 0; i < _cachedHostState.Length; i += 4096)
			_cachedHostState[i] = (byte)(cycle + i);

		_auditRows = Enumerable.Range(1, 40)
			.Select(index => new HostShortcutAuditRow(
				$"ACTION-{cycle + 1:000}-{index:000}",
				$"Shortcut payload row {index}",
				index % 2 == 0 ? "Pinned" : "Recent"))
			.ToArray();
	}

	public int Cycle { get; }

	public long PayloadBytes { get; }

	public void HandleAppAction(AppAction appAction)
	{
		if (_auditRows.Count == 0)
			return;

		_cachedHostState[0] = (byte)(_cachedHostState[0] ^ appAction.Id.Length);
	}
}

internal sealed record HostShortcutAuditRow(string Id, string Summary, string Status);

internal sealed record ReproStats(
	ReproOptions Options,
	int TrackedCycles,
	int AliveMauiApps,
	int AlivePayloads,
	long RetainedPayloadBytes,
	MemorySnapshot Baseline,
	MemorySnapshot Current,
	TimeSpan Elapsed)
{
	public string ToSummary()
	{
		var expectedPayload = Options.PayloadBytesPerApp * TrackedCycles;
		var retainedPercent = expectedPayload == 0 ? 0 : RetainedPayloadBytes * 100.0 / expectedPayload;

		return string.Join(Environment.NewLine,
			$"Run: {Options.Name}",
			$"MauiApps built and disposed: {TrackedCycles} in {Elapsed:mm\\:ss}",
			$"ConfigureEssentials OnAppAction handler: {(Options.UsesConfigureEssentialsOnAppAction ? "yes" : "no")}",
			$"Weak refs still alive after full GC:",
			$"  MauiApp instances: {AliveMauiApps}/{TrackedCycles}",
			$"  app-action payloads: {AlivePayloads}/{TrackedCycles}",
			$"Payload retained by alive handlers: {FormatBytes(RetainedPayloadBytes)} ({retainedPercent:0.0}% of allocated payload)",
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
