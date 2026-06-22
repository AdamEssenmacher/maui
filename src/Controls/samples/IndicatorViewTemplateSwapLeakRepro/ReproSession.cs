using System.Diagnostics;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Controls.Shapes;
using Microsoft.Maui.Graphics;

namespace IndicatorViewTemplateSwapLeakRepro;

internal enum ReproMode
{
	StaticTemplateControl,
	DirectTemplateReplace,
	ClearThenReplaceMitigation
}

internal sealed record ReproOptions(
	ReproMode Mode,
	int TemplateStateCount,
	int IndicatorItems,
	int PayloadKilobytesPerIndicator,
	int PostGcPositionUpdates)
{
	public long PayloadBytesPerIndicator => PayloadKilobytesPerIndicator * 1024L;

	public int ExpectedRetiredGenerations => Math.Max(0, TemplateStateCount - 1);

	public long ExpectedRetiredPayloadBytes => ExpectedRetiredGenerations * IndicatorItems * PayloadBytesPerIndicator;

	public string Name => Mode switch
	{
		ReproMode.StaticTemplateControl => "control: keep one non-null template",
		ReproMode.DirectTemplateReplace => "leak: direct non-null template replacement",
		ReproMode.ClearThenReplaceMitigation => "mitigation: clear template before replacement",
		_ => Mode.ToString()
	};
}

internal sealed class ReproSession
{
	readonly List<TrackedLayoutGeneration> _retiredGenerations = new();
	readonly Stopwatch _elapsed = Stopwatch.StartNew();
	int _realizedTemplateStates;

	public ReproSession(ReproOptions options)
	{
		Options = options;
		Stories = CreateStories(options.IndicatorItems);
	}

	public ReproOptions Options { get; }

	public IReadOnlyList<MediaStoryCard> Stories { get; }

	public void RecordMaterializedTemplateState(int generationIndex)
	{
		_realizedTemplateStates = Math.Max(_realizedTemplateStates, generationIndex + 1);
	}

	public void TrackRetiredGeneration(int generationIndex, string templateName, Layout layout, IReadOnlyList<RetainedPayloadBehavior> payloadBehaviors)
	{
		_retiredGenerations.Add(new TrackedLayoutGeneration(
			generationIndex,
			templateName,
			new WeakReference(layout),
			payloadBehaviors.Select(static behavior => new WeakReference(behavior)).ToArray(),
			payloadBehaviors.Sum(static behavior => behavior.PayloadBytes)));
	}

	public ReproStats GetStats(MemorySnapshot baseline, MemorySnapshot current, TimeSpan baselineBurst, TimeSpan postRunBurst)
	{
		var aliveLayouts = 0;
		var alivePayloadBehaviors = 0;
		long retainedPayloadBytes = 0;
		var totalPayloadBehaviors = 0;

		foreach (var generation in _retiredGenerations)
		{
			if (generation.Layout.IsAlive)
				aliveLayouts++;

			totalPayloadBehaviors += generation.PayloadBehaviors.Count;
			foreach (var behavior in generation.PayloadBehaviors)
			{
				if (!behavior.IsAlive)
					continue;

				alivePayloadBehaviors++;
			}

			if (generation.Layout.IsAlive)
				retainedPayloadBytes += generation.TotalPayloadBytes;
		}

		return new ReproStats(
			Options,
			_realizedTemplateStates,
			_retiredGenerations.Count,
			aliveLayouts,
			totalPayloadBehaviors,
			alivePayloadBehaviors,
			retainedPayloadBytes,
			baselineBurst,
			postRunBurst,
			baseline,
			current,
			_elapsed.Elapsed);
	}

	static IReadOnlyList<MediaStoryCard> CreateStories(int count)
	{
		var seed = new[]
		{
			new MediaStorySeed("Runway refresh", "Preview assets queued for the spring launch.", "Watch", "Ava Miles", "18 m", Color.FromArgb("#A63D40")),
			new MediaStorySeed("Partner webinar", "Captions and social cutdowns are ready for review.", "Ready", "Liam Chen", "42 m", Color.FromArgb("#146C5A")),
			new MediaStorySeed("Case study reel", "Customer story B-roll is waiting on color pass.", "At risk", "Noah Reed", "27 m", Color.FromArgb("#8A5A44")),
			new MediaStorySeed("Store signage", "Retail preview thumbnails have new localization variants.", "Ready", "Mia Park", "14 m", Color.FromArgb("#2E5AAC")),
			new MediaStorySeed("Launch trailer", "Narration mix and poster crops are in final review.", "Review", "Ethan Cole", "31 m", Color.FromArgb("#5E3AA1")),
			new MediaStorySeed("Creator pack", "Short-form edits are cached for field enablement.", "Watch", "Zoe Patel", "22 m", Color.FromArgb("#A05A14")),
			new MediaStorySeed("Field guide", "Regional preview snippets landed overnight.", "Ready", "Mason King", "16 m", Color.FromArgb("#0E7490")),
			new MediaStorySeed("Press kit", "Hero frames and thumbnails were regenerated today.", "Review", "Ella Brooks", "19 m", Color.FromArgb("#9A3412"))
		};

		var stories = new List<MediaStoryCard>(count);
		for (var index = 0; index < count; index++)
		{
			var entry = seed[index % seed.Length];
			var creatorInitials = string.Concat(entry.Creator.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(part => char.ToUpperInvariant(part[0])));
			stories.Add(new MediaStoryCard(
				index + 1,
				entry.Title,
				entry.Subtitle,
				entry.Status,
				entry.Creator,
				creatorInitials,
				entry.DurationText,
				entry.AccentColor));
		}

		return stories;
	}

	readonly record struct MediaStorySeed(
		string Title,
		string Subtitle,
		string Status,
		string Creator,
		string DurationText,
		Color AccentColor);
}

internal sealed record MediaStoryCard(
	int Id,
	string Title,
	string Subtitle,
	string Status,
	string Creator,
	string CreatorInitials,
	string DurationText,
	Color AccentColor)
{
	public string PreviewCode => $"P{Id:00}";
}

internal sealed record TrackedLayoutGeneration(
	int GenerationIndex,
	string TemplateName,
	WeakReference Layout,
	IReadOnlyList<WeakReference> PayloadBehaviors,
	long TotalPayloadBytes);

internal sealed record ReproStats(
	ReproOptions Options,
	int RealizedTemplateStates,
	int RetiredGenerationsTracked,
	int AliveRetiredLayouts,
	int RetiredPayloadBehaviorCount,
	int AliveRetiredPayloadBehaviors,
	long RetainedPayloadBytes,
	TimeSpan BaselinePositionBurst,
	TimeSpan PostRunPositionBurst,
	MemorySnapshot Baseline,
	MemorySnapshot Current,
	TimeSpan Elapsed)
{
	public string ToSummary()
	{
		var retainedPercent = Options.ExpectedRetiredPayloadBytes == 0
			? 0
			: RetainedPayloadBytes * 100.0 / Options.ExpectedRetiredPayloadBytes;
		var slowdown = BaselinePositionBurst.TotalMilliseconds <= 0
			? 0
			: PostRunPositionBurst.TotalMilliseconds / BaselinePositionBurst.TotalMilliseconds;

		return string.Join(Environment.NewLine,
			$"Run: {Options.Name}",
			$"Template states configured: {Options.TemplateStateCount}",
			$"Template states realized: {RealizedTemplateStates}",
			$"Retired layouts tracked: {RetiredGenerationsTracked}",
			$"Indicator items per layout: {Options.IndicatorItems}",
			$"Weak refs still alive after full GC:",
			$"  retired layouts: {AliveRetiredLayouts}/{RetiredGenerationsTracked}",
			$"  retired payload behaviors: {AliveRetiredPayloadBehaviors}/{RetiredPayloadBehaviorCount}",
			$"Retained retired payload: {FormatBytes(RetainedPayloadBytes)} ({retainedPercent:0.0}% of retired payload budget)",
			$"Position update burst after full GC:",
			$"  baseline: {FormatBurst(BaselinePositionBurst, Options.PostGcPositionUpdates)}",
			$"  post-run: {FormatBurst(PostRunPositionBurst, Options.PostGcPositionUpdates)}",
			$"  slowdown: {(slowdown > 0 ? $"{slowdown:0.0}x" : "n/a")}",
			$"Managed heap delta after GC: {FormatBytes(Current.ManagedBytes - Baseline.ManagedBytes)}",
			$"GC heap delta after GC: {FormatBytes(Current.GcHeapBytes - Baseline.GcHeapBytes)}",
			$"Resident memory delta: {FormatBytes(Current.ResidentBytes - Baseline.ResidentBytes)}",
			$"Working set delta: {FormatBytes(Current.WorkingSetBytes - Baseline.WorkingSetBytes)}",
			$"Elapsed: {Elapsed:mm\\:ss}");
	}

	static string FormatBurst(TimeSpan elapsed, int updateCount)
	{
		if (updateCount <= 0)
			return "disabled";

		return $"{elapsed.TotalMilliseconds:0.0} ms for {updateCount} updates";
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
