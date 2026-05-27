namespace MapGeopathAppendRepro;

internal enum ReproStatus
{
	Running,
	Completed,
	Reproduced,
	NotSupported,
	Failed
}

internal sealed record ReproResult(
	ReproOptions Options,
	ReproStatus Status,
	DateTimeOffset StartedAt,
	TimeSpan Elapsed,
	RuntimeImpact Impact,
	string Platform,
	int LogicalPointCount,
	int? RetainedOptionsPointCountBeforeMutation,
	int? RetainedOptionsPointCountAfterMutation,
	int? RetainedOptionsPointCountAfterReAdd,
	int? NativePolylinePointCountAfterReAdd,
	string? Message)
{
	public double? ObservedInflationRatio
	{
		get
		{
			var observedCount = NativePolylinePointCountAfterReAdd ??
				RetainedOptionsPointCountAfterReAdd ??
				RetainedOptionsPointCountAfterMutation;

			if (observedCount is null || LogicalPointCount == 0)
				return null;

			return (double)observedCount.Value / LogicalPointCount;
		}
	}

	public int? ExtraRetainedOptionsPointEntriesAfterMutation => SubtractLogicalPointCount(RetainedOptionsPointCountAfterMutation);

	public int? ExtraRetainedOptionsPointEntriesAfterReAdd => SubtractLogicalPointCount(RetainedOptionsPointCountAfterReAdd);

	public int? ExtraNativePolylinePointEntriesAfterReAdd => SubtractLogicalPointCount(NativePolylinePointCountAfterReAdd);

	public int? ExtraRetainedAndNativePointEntriesAfterReAdd
	{
		get
		{
			var retainedExtra = ExtraRetainedOptionsPointEntriesAfterReAdd ?? ExtraRetainedOptionsPointEntriesAfterMutation;
			var nativeExtra = ExtraNativePolylinePointEntriesAfterReAdd;

			if (retainedExtra is null && nativeExtra is null)
				return null;

			return (retainedExtra ?? 0) + (nativeExtra ?? 0);
		}
	}

	public long? MinimumExtraCoordinatePayloadBytes => ExtraRetainedAndNativePointEntriesAfterReAdd is int entries
		? entries * 16L
		: null;

	public string ImpactSummary => Status is ReproStatus.Reproduced
		? $"+{FormatNullable(ExtraNativePolylinePointEntriesAfterReAdd)} unnecessary native points; +{FormatNullable(ExtraRetainedAndNativePointEntriesAfterReAdd)} unnecessary retained+native point entries; at least {FormatBytes(MinimumExtraCoordinatePayloadBytes)} duplicated coordinate payload; {FormatMilliseconds(Impact.MeasuredElapsed)} measured; {FormatBytes(Impact.ManagedAllocatedBytesDelta)} managed allocated"
		: Status is ReproStatus.Completed
			? $"No unnecessary native points; {FormatMilliseconds(Impact.MeasuredElapsed)} measured; {FormatBytes(Impact.ManagedAllocatedBytesDelta)} managed allocated"
			: "unavailable";

	public static ReproResult CreateRunning(ReproOptions options, DateTimeOffset startedAt)
	{
		return new ReproResult(
			options,
			ReproStatus.Running,
			startedAt,
			TimeSpan.Zero,
			RuntimeImpact.Empty,
			MapDiagnostics.PlatformName,
			options.LogicalPointCount,
			null,
			null,
			null,
			null,
			"Started.");
	}

	public string ToOutput()
	{
		return string.Join(Environment.NewLine,
			$"Run: {Options.Name}",
			$"Status: {Status}",
			$"Impact summary: {ImpactSummary}",
			$"Platform: {Platform}",
			$"Started: {StartedAt:O}",
			$"Elapsed: {Elapsed:c}",
			$"Measured scenario elapsed: {FormatMilliseconds(Impact.MeasuredElapsed)}",
			$"Initial render elapsed: {FormatMilliseconds(Impact.InitialRenderElapsed)}",
			$"Off-map mutation elapsed: {FormatMilliseconds(Impact.OffMapMutationElapsed)}",
			$"Re-add elapsed: {FormatMilliseconds(Impact.ReAddElapsed)}",
			$"Managed allocated bytes during scenario: {FormatBytes(Impact.ManagedAllocatedBytesDelta)}",
			$"Managed heap delta during scenario: {FormatBytes(Impact.ManagedHeapBytesDelta)}",
			$"Android Java heap delta during scenario: {FormatBytes(Impact.AndroidJavaHeapBytesDelta)}",
			$"Mutation API: {Options.MutationApiName}",
			$"Initial route points: {Options.InitialPointCount}",
			$"Appended route points: {Options.AppendedPointCount}",
			$"Logical MAUI route points: {LogicalPointCount}",
			$"Expected point count if options are idempotent: {Options.ExpectedIdempotentPointCount}",
			$"Expected inflated retained options points: {FormatNullable(Options.ExpectedInflatedPointCount)}",
			$"Retained options points before mutation: {FormatNullable(RetainedOptionsPointCountBeforeMutation)}",
			$"Retained options points after mutation: {FormatNullable(RetainedOptionsPointCountAfterMutation)}",
			$"Retained options points after re-add: {FormatNullable(RetainedOptionsPointCountAfterReAdd)}",
			$"Native Android polyline points after re-add: {FormatNullable(NativePolylinePointCountAfterReAdd)}",
			$"Observed native/options inflation ratio: {FormatRatio(ObservedInflationRatio)}",
			$"Unnecessary retained options point entries after mutation: {FormatNullable(ExtraRetainedOptionsPointEntriesAfterMutation)}",
			$"Unnecessary native point entries after re-add: {FormatNullable(ExtraNativePolylinePointEntriesAfterReAdd)}",
			$"Unnecessary retained+native point entries after re-add: {FormatNullable(ExtraRetainedAndNativePointEntriesAfterReAdd)}",
			$"Minimum unnecessary duplicated coordinate payload retained+native: {FormatBytes(MinimumExtraCoordinatePayloadBytes)}",
			$"Step delay milliseconds: {Options.StepDelayMilliseconds}",
			string.IsNullOrWhiteSpace(Message) ? "Message: none" : $"Message: {Message}");
	}

	public string ToDisplayText()
	{
		return string.Join(Environment.NewLine,
			$"{Options.Name}: {Status}",
			$"Logical points: {LogicalPointCount}",
			$"Expected idempotent: {Options.ExpectedIdempotentPointCount}",
			$"Expected inflated: {FormatNullable(Options.ExpectedInflatedPointCount)}",
			$"Retained options after mutation: {FormatNullable(RetainedOptionsPointCountAfterMutation)}",
			$"Native after re-add: {FormatNullable(NativePolylinePointCountAfterReAdd)}",
			$"Inflation: {FormatRatio(ObservedInflationRatio)}",
			$"Unnecessary entries: {FormatNullable(ExtraRetainedAndNativePointEntriesAfterReAdd)}",
			$"Unnecessary payload: {FormatBytes(MinimumExtraCoordinatePayloadBytes)}",
			$"Measured elapsed: {FormatMilliseconds(Impact.MeasuredElapsed)}",
			$"Managed allocated: {FormatBytes(Impact.ManagedAllocatedBytesDelta)}",
			string.IsNullOrWhiteSpace(Message) ? string.Empty : Message).Trim();
	}

	int? SubtractLogicalPointCount(int? value)
	{
		if (value is null)
			return null;

		return Math.Max(0, value.Value - LogicalPointCount);
	}

	static string FormatNullable(int? value)
	{
		return value?.ToString() ?? "unavailable";
	}

	static string FormatRatio(double? value)
	{
		return value is null ? "unavailable" : $"{value:0.00}x";
	}

	static string FormatMilliseconds(TimeSpan value)
	{
		return $"{value.TotalMilliseconds:0.0} ms";
	}

	static string FormatBytes(long? bytes)
	{
		return bytes is null
			? "unavailable"
			: $"{bytes.Value / 1024d / 1024d:0.00} MB ({bytes.Value} bytes)";
	}
}
