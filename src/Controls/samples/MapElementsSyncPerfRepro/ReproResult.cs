namespace MapElementsSyncPerfRepro;

internal enum ReproStatus
{
	Running,
	Completed,
	Hung,
	Failed
}

internal sealed record ReproResult(
	ReproOptions Options,
	ReproStatus Status,
	DateTimeOffset StartedAt,
	TimeSpan Elapsed,
	int GeneratedElements,
	int AddedElements,
	int MapElementCount,
	TimeSpan GenerationElapsed,
	TimeSpan AddElapsed,
	TimeSpan ObservationElapsed,
	long ManagedMemoryBefore,
	long ManagedMemoryAfter,
	int HeartbeatCount,
	DateTimeOffset LastHeartbeatAt,
	TimeSpan MaxHeartbeatGap,
	string? Message)
{
	public TimeSpan WallClockUpdateCost
	{
		get
		{
			var observation = TimeSpan.FromSeconds(Options.PostAddObservationSeconds);
			var settle = Options.Scenario is ReproScenario.LiveBurstAdd or ReproScenario.LivePacedAdd
				? TimeSpan.FromMilliseconds(Options.LiveMapSettleMilliseconds)
				: TimeSpan.Zero;
			var updateCost = Elapsed - observation - settle;

			return updateCost > TimeSpan.Zero ? updateCost : TimeSpan.Zero;
		}
	}

	public double EffectiveElementsPerSecond =>
		AddedElements > 0 && WallClockUpdateCost.TotalSeconds > 0
			? AddedElements / WallClockUpdateCost.TotalSeconds
			: 0;

	public long ManagedMemoryDelta => ManagedMemoryAfter - ManagedMemoryBefore;

	public string ToOutput()
	{
		return string.Join(Environment.NewLine,
			$"Run: {Options.Name}",
			$"Status: {Status}",
			$"Started: {StartedAt:O}",
			$"Elapsed: {Elapsed:c}",
			$"Element kind: {Options.ElementKind}",
			$"Configured elements: {Options.ElementCount}",
			$"Scenario adds to Map.MapElements: {Options.AddsToMap}",
			$"Generated elements: {GeneratedElements}/{Options.ElementCount}",
			$"Added elements: {AddedElements}/{Options.ElementCount}",
			$"Current Map.MapElements count: {MapElementCount}",
			$"Generation elapsed: {GenerationElapsed:c}",
			$"Add elapsed: {AddElapsed:c}",
			$"Observation elapsed: {ObservationElapsed:c}",
			$"Wall-clock update cost: {WallClockUpdateCost:c}",
			$"Effective throughput: {EffectiveElementsPerSecond:0.0} elements/s",
			$"Managed memory before: {ManagedMemoryBefore}",
			$"Managed memory after: {ManagedMemoryAfter}",
			$"Managed memory delta: {ManagedMemoryDelta}",
			$"UI heartbeats: {HeartbeatCount}",
			$"Last UI heartbeat: {LastHeartbeatAt:O}",
			$"Max UI heartbeat gap: {MaxHeartbeatGap.TotalMilliseconds:0} ms",
			$"Seed: {Options.Seed}",
			$"Watchdog timeout seconds: {Options.WatchdogTimeoutSeconds}",
			$"Post-add observation seconds: {Options.PostAddObservationSeconds}",
			$"Live map settle milliseconds: {Options.LiveMapSettleMilliseconds}",
			$"Paced add delay milliseconds: {Options.PacedAddDelayMilliseconds}",
			string.IsNullOrWhiteSpace(Message) ? "Message: none" : $"Message: {Message}");
	}

	public string ToDisplayText()
	{
		return string.Join(Environment.NewLine,
			$"{Options.Name}: {Status}",
			$"Elapsed: {Elapsed:c}",
			$"Generated: {GeneratedElements}/{Options.ElementCount}",
			$"Added: {AddedElements}/{Options.ElementCount}",
			$"Map elements: {MapElementCount}",
			$"Generation: {GenerationElapsed.TotalMilliseconds:0} ms",
			$"Add loop: {AddElapsed.TotalMilliseconds:0} ms",
			$"Wall-clock update: {WallClockUpdateCost.TotalMilliseconds:0} ms",
			$"Throughput: {EffectiveElementsPerSecond:0.0} elements/s",
			$"Max heartbeat gap: {MaxHeartbeatGap.TotalMilliseconds:0} ms",
			$"Managed delta: {ManagedMemoryDelta:n0} bytes",
			string.IsNullOrWhiteSpace(Message) ? string.Empty : Message).Trim();
	}
}
