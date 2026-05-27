namespace MapGeopathAppendRepro;

internal enum ReproScenario
{
	FreshInstanceControl,
	RetainedGeopathCollectionMutation,
	RetainedPolylineAddMutation
}

internal sealed record ReproOptions(
	ReproScenario Scenario,
	int InitialPointCount,
	int AppendedPointCount,
	int StepDelayMilliseconds)
{
	public int LogicalPointCount => InitialPointCount + AppendedPointCount;

	public int HandlerGeopathUpdatesPerAppend => Scenario == ReproScenario.RetainedPolylineAddMutation ? 2 : 1;

	public int ExpectedIdempotentPointCount => LogicalPointCount;

	public int? ExpectedInflatedPointCount => Scenario == ReproScenario.FreshInstanceControl
		? null
		: InitialPointCount + HandlerGeopathUpdatesPerAppend * SumAppendedLogicalPointCounts();

	public bool UsesPolylineAddApi => Scenario == ReproScenario.RetainedPolylineAddMutation;

	public string MutationApiName => Scenario switch
	{
		ReproScenario.FreshInstanceControl => "none",
		ReproScenario.RetainedGeopathCollectionMutation => "polyline.Geopath.Add",
		ReproScenario.RetainedPolylineAddMutation => "polyline.Add",
		_ => Scenario.ToString()
	};

	public string Name => Scenario switch
	{
		ReproScenario.FreshInstanceControl => "fresh instance control",
		ReproScenario.RetainedGeopathCollectionMutation => "retained Geopath.Add repro",
		ReproScenario.RetainedPolylineAddMutation => "retained Polyline.Add repro",
		_ => Scenario.ToString()
	};

	public static ReproOptions CreateFreshInstanceControl(int initialPointCount, int appendedPointCount, int stepDelayMilliseconds)
	{
		return new ReproOptions(
			ReproScenario.FreshInstanceControl,
			initialPointCount,
			appendedPointCount,
			stepDelayMilliseconds);
	}

	public static ReproOptions CreateRetainedGeopathCollectionMutation(int initialPointCount, int appendedPointCount, int stepDelayMilliseconds)
	{
		return new ReproOptions(
			ReproScenario.RetainedGeopathCollectionMutation,
			initialPointCount,
			appendedPointCount,
			stepDelayMilliseconds);
	}

	public static ReproOptions CreateRetainedPolylineAddMutation(int initialPointCount, int appendedPointCount, int stepDelayMilliseconds)
	{
		return new ReproOptions(
			ReproScenario.RetainedPolylineAddMutation,
			initialPointCount,
			appendedPointCount,
			stepDelayMilliseconds);
	}

	int SumAppendedLogicalPointCounts()
	{
		var firstAppendedLogicalCount = InitialPointCount + 1;
		var lastAppendedLogicalCount = LogicalPointCount;

		return (firstAppendedLogicalCount + lastAppendedLogicalCount) * AppendedPointCount / 2;
	}
}
