namespace MapElementsPerfRepro;

internal enum ReproScenario
{
	SmallBaseline,
	GenerationControl,
	IssueRepro
}

internal sealed record ReproOptions(
	ReproScenario Scenario,
	int PolylineCount,
	int PointsPerPolyline,
	int Seed,
	int WatchdogTimeoutSeconds,
	int PostRenderObservationSeconds,
	int ProgressLogInterval,
	bool AddToMap)
{
	public long TotalLocations => (long)PolylineCount * PointsPerPolyline;

	public string Name => Scenario switch
	{
		ReproScenario.SmallBaseline => "small baseline",
		ReproScenario.GenerationControl => "generation control",
		ReproScenario.IssueRepro => "issue repro",
		_ => Scenario.ToString()
	};

	public static ReproOptions CreateSmallBaseline(int seed, int watchdogTimeoutSeconds, int progressLogInterval)
	{
		return new ReproOptions(
			ReproScenario.SmallBaseline,
			PolylineCount: 8,
			PointsPerPolyline: 80,
			seed,
			watchdogTimeoutSeconds,
			PostRenderObservationSeconds: 2,
			progressLogInterval,
			AddToMap: true);
	}

	public static ReproOptions CreateGenerationControl(
		int polylineCount,
		int pointsPerPolyline,
		int seed,
		int watchdogTimeoutSeconds,
		int progressLogInterval)
	{
		return new ReproOptions(
			ReproScenario.GenerationControl,
			polylineCount,
			pointsPerPolyline,
			seed,
			watchdogTimeoutSeconds,
			PostRenderObservationSeconds: 2,
			progressLogInterval,
			AddToMap: false);
	}

	public static ReproOptions CreateIssueRepro(
		int polylineCount,
		int pointsPerPolyline,
		int seed,
		int watchdogTimeoutSeconds,
		int progressLogInterval)
	{
		return new ReproOptions(
			ReproScenario.IssueRepro,
			polylineCount,
			pointsPerPolyline,
			seed,
			watchdogTimeoutSeconds,
			PostRenderObservationSeconds: Math.Min(30, watchdogTimeoutSeconds),
			progressLogInterval,
			AddToMap: true);
	}
}
