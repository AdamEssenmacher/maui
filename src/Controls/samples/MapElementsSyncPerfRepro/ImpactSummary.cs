using System.Text;

namespace MapElementsSyncPerfRepro;

internal static class ImpactSummary
{
	public static string Create(IReadOnlyList<ReproResult> results)
	{
		var generation = Find(results, ReproScenario.GenerationControl);
		var detached = Find(results, ReproScenario.DetachedPopulate);
		var liveBurst = Find(results, ReproScenario.LiveBurstAdd);
		var livePaced = Find(results, ReproScenario.LivePacedAdd);
		var primary = detached ?? liveBurst ?? livePaced ?? generation;
		var builder = new StringBuilder();

		builder.AppendLine("Before/After Impact Summary");
		builder.AppendLine($"Workload: {primary?.Options.ElementCount ?? 0:n0} {primary?.Options.ElementKind.ToString() ?? "map"} elements");
		builder.AppendLine("After approximation: detached populate, which produces one initial platform sync.");
		builder.AppendLine("Before/current behavior: live Map.MapElements.Add, which updates the handler once per add.");
		builder.AppendLine();

		if (generation is not null)
		{
			builder.AppendLine("Generation Control");
			builder.AppendLine($"Element creation: {FormatDuration(generation.GenerationElapsed)} ({FormatRate(generation.GeneratedElements, generation.GenerationElapsed)})");
			builder.AppendLine();
		}

		builder.AppendLine("A/B Metrics");
		builder.AppendLine("Metric | After approximation | Before live burst | Before live paced");
		builder.AppendLine("--- | ---: | ---: | ---:");
		AppendMetricRow(builder, "Status", detached?.Status.ToString(), liveBurst?.Status.ToString(), livePaced?.Status.ToString());
		AppendMetricRow(builder, "Wall-clock update cost", FormatDurationOrMissing(GetUpdateCost(detached)), FormatDurationOrMissing(GetUpdateCost(liveBurst)), FormatDurationOrMissing(GetUpdateCost(livePaced)));
		AppendMetricRow(builder, "Effective throughput", FormatThroughput(detached), FormatThroughput(liveBurst), FormatThroughput(livePaced));
		AppendMetricRow(builder, "Managed add loop only", FormatDurationOrMissing(detached?.AddElapsed), FormatDurationOrMissing(liveBurst?.AddElapsed), FormatDurationOrMissing(livePaced?.AddElapsed));
		AppendMetricRow(builder, "Max UI heartbeat gap", FormatDurationOrMissing(detached?.MaxHeartbeatGap), FormatDurationOrMissing(liveBurst?.MaxHeartbeatGap), FormatDurationOrMissing(livePaced?.MaxHeartbeatGap));
		AppendMetricRow(builder, "Final managed memory delta", FormatBytesOrMissing(detached), FormatBytesOrMissing(liveBurst), FormatBytesOrMissing(livePaced));
		builder.AppendLine();

		builder.AppendLine("Real-World Improvement If Fixed");
		builder.AppendLine("Scenario | User wait saved | Waiting reduction | Worst UI freeze saved | Freeze reduction | Throughput gain");
		builder.AppendLine("--- | ---: | ---: | ---: | ---: | ---:");
		AppendImprovementRow(builder, "Live burst add", detached, liveBurst);
		AppendImprovementRow(builder, "Live paced add", detached, livePaced);
		builder.AppendLine();

		AppendComparison(builder, "Live burst", detached, liveBurst);
		AppendComparison(builder, "Live paced", detached, livePaced);

		builder.AppendLine();
		builder.AppendLine("Interpretation");
		builder.AppendLine("User wait saved is the clearest number: it is how much sooner the map finishes reflecting all elements if the repeated live sync work is replaced by one effective sync.");
		builder.AppendLine("Wall-clock update cost subtracts the configured observation window and live-map settle delay, so it captures the user-visible cost of making the map reflect the new elements.");
		builder.AppendLine("Live burst shows the freeze problem: managed Add returns quickly, then queued native work blocks the UI afterward.");
		builder.AppendLine("Live paced shows the streaming-update problem: the UI keeps heartbeating, but the user waits much longer for all elements to finish appearing.");

		return builder.ToString().TrimEnd();
	}

	static ReproResult? Find(IReadOnlyList<ReproResult> results, ReproScenario scenario)
	{
		for (var index = results.Count - 1; index >= 0; index--)
		{
			if (results[index].Options.Scenario == scenario)
				return results[index];
		}

		return null;
	}

	static void AppendMetricRow(StringBuilder builder, string metric, string? after, string? burst, string? paced)
	{
		builder.AppendLine($"{metric} | {ValueOrMissing(after)} | {ValueOrMissing(burst)} | {ValueOrMissing(paced)}");
	}

	static void AppendComparison(StringBuilder builder, string name, ReproResult? after, ReproResult? before)
	{
		if (after is null || before is null)
		{
			builder.AppendLine($"{name}: missing comparison data.");
			return;
		}

		var afterCost = GetUpdateCost(after);
		var beforeCost = GetUpdateCost(before);
		var costFactor = Divide(beforeCost.TotalMilliseconds, afterCost.TotalMilliseconds);
		var stallFactor = Divide(before.MaxHeartbeatGap.TotalMilliseconds, after.MaxHeartbeatGap.TotalMilliseconds);
		var stallDelta = before.MaxHeartbeatGap - after.MaxHeartbeatGap;

		builder.AppendLine(
			$"{name}: {FormatFactor(costFactor)} wall-clock update cost, {FormatFactor(stallFactor)} max UI stall, {FormatSignedDuration(stallDelta)} heartbeat-gap delta vs after approximation.");
	}

	static void AppendImprovementRow(StringBuilder builder, string name, ReproResult? after, ReproResult? before)
	{
		if (after is null || before is null)
		{
			builder.AppendLine($"{name} | missing | missing | missing | missing | missing");
			return;
		}

		var waitSaved = GetUpdateCost(before) - GetUpdateCost(after);
		var stallSaved = before.MaxHeartbeatGap - after.MaxHeartbeatGap;
		var throughputGain = after.EffectiveElementsPerSecond - before.EffectiveElementsPerSecond;

		builder.AppendLine(string.Join(" | ",
			name,
			FormatSignedMillisecondsSaved(waitSaved),
			FormatPercentReduction(GetUpdateCost(before), GetUpdateCost(after)),
			FormatSignedMillisecondsSaved(stallSaved),
			FormatPercentReduction(before.MaxHeartbeatGap, after.MaxHeartbeatGap),
			FormatSignedThroughputGain(throughputGain, before.EffectiveElementsPerSecond)));
	}

	static TimeSpan GetUpdateCost(ReproResult? result)
	{
		return result?.WallClockUpdateCost ?? TimeSpan.Zero;
	}

	static string FormatThroughput(ReproResult? result)
	{
		if (result is null)
			return "missing";

		if (result.EffectiveElementsPerSecond <= 0)
			return "n/a";

		return result.EffectiveElementsPerSecond >= 100
			? $"{result.EffectiveElementsPerSecond:0} elements/s"
			: $"{result.EffectiveElementsPerSecond:0.0} elements/s";
	}

	static string FormatRate(int count, TimeSpan elapsed)
	{
		if (count <= 0 || elapsed.TotalSeconds <= 0)
			return "n/a";

		var rate = count / elapsed.TotalSeconds;
		return rate >= 100
			? $"{rate:0} elements/s"
			: $"{rate:0.0} elements/s";
	}

	static string FormatDurationOrMissing(TimeSpan? duration)
	{
		return duration is null ? "missing" : FormatDuration(duration.Value);
	}

	static string FormatDuration(TimeSpan duration)
	{
		var sign = duration < TimeSpan.Zero ? "-" : string.Empty;
		duration = duration.Duration();

		if (duration.TotalMilliseconds < 1000)
			return $"{sign}{duration.TotalMilliseconds:0} ms";

		return $"{sign}{duration.TotalSeconds:0.00} s";
	}

	static string FormatSignedDuration(TimeSpan duration)
	{
		var sign = duration < TimeSpan.Zero ? string.Empty : "+";
		return $"{sign}{FormatDuration(duration)}";
	}

	static string FormatSignedMillisecondsSaved(TimeSpan saved)
	{
		var sign = saved < TimeSpan.Zero ? "-" : "+";
		return $"{sign}{Math.Abs(saved.TotalMilliseconds):0} ms";
	}

	static string FormatPercentReduction(TimeSpan before, TimeSpan after)
	{
		if (before.TotalMilliseconds <= 0)
			return "n/a";

		var reduction = (before.TotalMilliseconds - after.TotalMilliseconds) / before.TotalMilliseconds * 100;
		if (reduction < 0)
			return $"{Math.Abs(reduction):0.0}% worse";

		return $"{reduction:0.0}% less";
	}

	static string FormatSignedThroughputGain(double gain, double before)
	{
		var sign = gain < 0 ? "-" : "+";
		var percent = before > 0 ? gain / before * 100 : double.PositiveInfinity;
		var percentText = double.IsPositiveInfinity(percent)
			? "unbounded"
			: $"{percent:+0;-0;0}%";

		return $"{sign}{Math.Abs(gain):0.0} elements/s ({percentText})";
	}

	static string FormatBytesOrMissing(ReproResult? result)
	{
		return result is null ? "missing" : FormatBytes(result.ManagedMemoryDelta);
	}

	static string FormatBytes(long bytes)
	{
		var sign = bytes < 0 ? "-" : "+";
		var absoluteBytes = Math.Abs(bytes);
		var megabytes = absoluteBytes / 1024d / 1024d;

		return $"{sign}{megabytes:0.0} MB";
	}

	static double Divide(double numerator, double denominator)
	{
		if (denominator <= 0)
			return double.PositiveInfinity;

		return numerator / denominator;
	}

	static string FormatFactor(double factor)
	{
		if (double.IsPositiveInfinity(factor))
			return "unbounded";

		return factor >= 10 ? $"{factor:0.0}x" : $"{factor:0.00}x";
	}

	static string ValueOrMissing(string? value)
	{
		return string.IsNullOrWhiteSpace(value) ? "missing" : value;
	}
}
