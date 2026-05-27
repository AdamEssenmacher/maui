namespace MapElementsSyncPerfRepro;

internal enum MapElementKind
{
	Circle,
	ShortPolyline
}

internal enum ReproScenario
{
	GenerationControl,
	DetachedPopulate,
	LiveBurstAdd,
	LivePacedAdd
}

internal sealed record ReproOptions(
	ReproScenario Scenario,
	MapElementKind ElementKind,
	int ElementCount,
	int Seed,
	int WatchdogTimeoutSeconds,
	int PostAddObservationSeconds,
	int ProgressLogInterval,
	int LiveMapSettleMilliseconds,
	int PacedAddDelayMilliseconds)
{
	public bool AddsToMap => Scenario != ReproScenario.GenerationControl;

	public string Name => Scenario switch
	{
		ReproScenario.GenerationControl => "generation control",
		ReproScenario.DetachedPopulate => "detached populate",
		ReproScenario.LiveBurstAdd => "live burst add",
		ReproScenario.LivePacedAdd => "live paced add",
		_ => Scenario.ToString()
	};

	public static ReproOptions CreateGenerationControl(
		MapElementKind elementKind,
		int elementCount,
		int seed,
		int watchdogTimeoutSeconds,
		int postAddObservationSeconds,
		int progressLogInterval,
		int liveMapSettleMilliseconds,
		int pacedAddDelayMilliseconds)
	{
		return new ReproOptions(
			ReproScenario.GenerationControl,
			elementKind,
			elementCount,
			seed,
			watchdogTimeoutSeconds,
			postAddObservationSeconds,
			progressLogInterval,
			liveMapSettleMilliseconds,
			pacedAddDelayMilliseconds);
	}

	public static ReproOptions CreateDetachedPopulate(
		MapElementKind elementKind,
		int elementCount,
		int seed,
		int watchdogTimeoutSeconds,
		int postAddObservationSeconds,
		int progressLogInterval,
		int liveMapSettleMilliseconds,
		int pacedAddDelayMilliseconds)
	{
		return new ReproOptions(
			ReproScenario.DetachedPopulate,
			elementKind,
			elementCount,
			seed,
			watchdogTimeoutSeconds,
			postAddObservationSeconds,
			progressLogInterval,
			liveMapSettleMilliseconds,
			pacedAddDelayMilliseconds);
	}

	public static ReproOptions CreateLiveBurstAdd(
		MapElementKind elementKind,
		int elementCount,
		int seed,
		int watchdogTimeoutSeconds,
		int postAddObservationSeconds,
		int progressLogInterval,
		int liveMapSettleMilliseconds,
		int pacedAddDelayMilliseconds)
	{
		return new ReproOptions(
			ReproScenario.LiveBurstAdd,
			elementKind,
			elementCount,
			seed,
			watchdogTimeoutSeconds,
			postAddObservationSeconds,
			progressLogInterval,
			liveMapSettleMilliseconds,
			pacedAddDelayMilliseconds);
	}

	public static ReproOptions CreateLivePacedAdd(
		MapElementKind elementKind,
		int elementCount,
		int seed,
		int watchdogTimeoutSeconds,
		int postAddObservationSeconds,
		int progressLogInterval,
		int liveMapSettleMilliseconds,
		int pacedAddDelayMilliseconds)
	{
		return new ReproOptions(
			ReproScenario.LivePacedAdd,
			elementKind,
			elementCount,
			seed,
			watchdogTimeoutSeconds,
			postAddObservationSeconds,
			progressLogInterval,
			liveMapSettleMilliseconds,
			pacedAddDelayMilliseconds);
	}
}
