namespace MapElementsPerfRepro;

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
	int GeneratedPolylines,
	int AddedPolylines,
	long GeneratedLocations,
	DateTimeOffset LastHeartbeatAt,
	TimeSpan MaxHeartbeatGap,
	string? Message)
{
	public string ToOutput()
	{
		return string.Join(Environment.NewLine,
			$"Run: {Options.Name}",
			$"Status: {Status}",
			$"Started: {StartedAt:O}",
			$"Elapsed: {Elapsed:c}",
			$"Scenario adds to Map.MapElements: {Options.AddToMap}",
			$"Configured polylines: {Options.PolylineCount}",
			$"Configured points/polyline: {Options.PointsPerPolyline}",
			$"Configured locations: {Options.TotalLocations}",
			$"Generated polylines: {GeneratedPolylines}/{Options.PolylineCount}",
			$"Added polylines: {AddedPolylines}/{Options.PolylineCount}",
			$"Generated locations: {GeneratedLocations}/{Options.TotalLocations}",
			$"Last UI heartbeat: {LastHeartbeatAt:O}",
			$"Max UI heartbeat gap: {MaxHeartbeatGap.TotalMilliseconds:0} ms",
			$"Seed: {Options.Seed}",
			$"Watchdog timeout seconds: {Options.WatchdogTimeoutSeconds}",
			$"Post-render observation seconds: {Options.PostRenderObservationSeconds}",
			string.IsNullOrWhiteSpace(Message) ? "Message: none" : $"Message: {Message}");
	}

	public string ToDisplayText()
	{
		return string.Join(Environment.NewLine,
			$"{Options.Name}: {Status}",
			$"Elapsed: {Elapsed:c}",
			$"Generated: {GeneratedPolylines}/{Options.PolylineCount} polylines",
			$"Added: {AddedPolylines}/{Options.PolylineCount} polylines",
			$"Locations: {GeneratedLocations}/{Options.TotalLocations}",
			$"Max heartbeat gap: {MaxHeartbeatGap.TotalMilliseconds:0} ms",
			string.IsNullOrWhiteSpace(Message) ? string.Empty : Message).Trim();
	}
}
