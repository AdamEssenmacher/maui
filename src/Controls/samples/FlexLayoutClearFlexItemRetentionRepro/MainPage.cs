using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Layouts;

namespace FlexLayoutClearFlexItemRetentionRepro;

public sealed class MainPage : ContentPage
{
	const int LayoutCount = 40;
	const int ChildrenPerLayout = 4;
	const int PayloadBytes = 1024 * 1024;
	const int SurvivorTolerance = 2;
	const int SentinelChildren = LayoutCount;
	const int SiblingChildren = LayoutCount * (ChildrenPerLayout - 1);

	static readonly BindableProperty FlexItemProperty =
		(BindableProperty)(typeof(FlexLayout).GetField("FlexItemProperty", BindingFlags.Static | BindingFlags.NonPublic)?.GetValue(null)
			?? throw new MissingFieldException(typeof(FlexLayout).FullName, "FlexItemProperty"));

	readonly string? _resultsPath;
	readonly Label _status;
	bool _started;

	public MainPage(string? resultsPath)
	{
		_resultsPath = resultsPath;
		Title = "FlexLayout Clear FlexItem Retention";

		_status = new Label
		{
			Text = "Running FlexLayout Clear FlexItem retention repro...",
			HorizontalOptions = LayoutOptions.Center,
			VerticalOptions = LayoutOptions.Center
		};

		Content = new Grid
		{
			Padding = 24,
			Children = { _status }
		};

		Loaded += OnLoaded;
	}

	async void OnLoaded(object? sender, EventArgs e)
	{
		if (_started)
			return;

		_started = true;

		try
		{
			var result = await RunReproAsync();
			var report = result.ToReport();

			_status.Text = result.Proven
				? "PROVEN: FlexLayout.Clear retained cleared sibling children through stale FlexItems."
				: "NOT PROVEN: cleared FlexLayout children did not remain alive.";

			if (!string.IsNullOrWhiteSpace(_resultsPath))
				File.WriteAllText(_resultsPath, report);

			await Task.Delay(250);
			Environment.Exit(result.Proven ? 0 : 2);
		}
		catch (Exception ex)
		{
			var report = "FlexLayout Clear FlexItem retention repro failed." + Environment.NewLine + ex;
			_status.Text = report;

			if (!string.IsNullOrWhiteSpace(_resultsPath))
				File.WriteAllText(_resultsPath, report);

			await Task.Delay(250);
			Environment.Exit(3);
		}
	}

	static async Task<ReproResult> RunReproAsync()
	{
		var baseline = await RunScenarioAsync("Baseline: Clear with no retained child", RemovalMode.Clear, retainSentinel: false);
		var control = await RunScenarioAsync("Control: RemoveAt with one retained removed child", RemovalMode.RemoveAt, retainSentinel: true);
		var current = await RunScenarioAsync("Current MAUI behavior: Clear with one retained removed child", RemovalMode.Clear, retainSentinel: true);

		var baselineCollected = baseline.LayoutSurvivors <= SurvivorTolerance
			&& baseline.SiblingChildSurvivors <= SurvivorTolerance
			&& baseline.SiblingPayloadSurvivors <= SurvivorTolerance
			&& baseline.SiblingPayloadBufferSurvivors <= SurvivorTolerance;

		var controlCollected = control.LayoutSurvivors <= SurvivorTolerance
			&& control.SentinelChildSurvivors >= SentinelChildren - SurvivorTolerance
			&& control.StaleFlexItemReferences <= SurvivorTolerance
			&& control.SiblingChildSurvivors <= SurvivorTolerance
			&& control.SiblingPayloadSurvivors <= SurvivorTolerance
			&& control.SiblingPayloadBufferSurvivors <= SurvivorTolerance;

		var currentRetained = current.LayoutSurvivors <= SurvivorTolerance
			&& current.SentinelChildSurvivors >= SentinelChildren - SurvivorTolerance
			&& current.StaleFlexItemReferences >= SentinelChildren - SurvivorTolerance
			&& current.SiblingChildSurvivors >= SiblingChildren - SurvivorTolerance
			&& current.SiblingPayloadSurvivors >= SiblingChildren - SurvivorTolerance
			&& current.SiblingPayloadBufferSurvivors >= SiblingChildren - SurvivorTolerance;

		return new ReproResult(baseline, control, current, baselineCollected && controlCollected && currentRetained);
	}

	static async Task<ScenarioResult> RunScenarioAsync(string name, RemovalMode removalMode, bool retainSentinel)
	{
		var retainedSentinels = new List<Label>(retainSentinel ? LayoutCount : 0);
		var layoutRefs = new List<WeakReference<FlexLayout>>(LayoutCount);
		var sentinelChildRefs = new List<WeakReference<Label>>(LayoutCount);
		var sentinelPayloadRefs = new List<WeakReference<Payload>>(LayoutCount);
		var sentinelPayloadBufferRefs = new List<WeakReference<byte[]>>(LayoutCount);
		var siblingChildRefs = new List<WeakReference<Label>>(SiblingChildren);
		var siblingPayloadRefs = new List<WeakReference<Payload>>(SiblingChildren);
		var siblingPayloadBufferRefs = new List<WeakReference<byte[]>>(SiblingChildren);

		ForceFullGc();
		var heapBefore = GC.GetTotalMemory(forceFullCollection: true);

		for (var layoutIndex = 0; layoutIndex < LayoutCount; layoutIndex++)
		{
			CreateClearedFlexLayoutScenario(
				layoutIndex,
				removalMode,
				retainSentinel,
				retainedSentinels,
				layoutRefs,
				sentinelChildRefs,
				sentinelPayloadRefs,
				sentinelPayloadBufferRefs,
				siblingChildRefs,
				siblingPayloadRefs,
				siblingPayloadBufferRefs);

			if (layoutIndex % 8 == 0)
				await Task.Yield();
		}

		await WaitForCollectionAsync();
		var heapAfter = GC.GetTotalMemory(forceFullCollection: true);

		var result = new ScenarioResult(
			name,
			removalMode,
			retainSentinel,
			retainedSentinels.Count,
			CountStaleFlexItemReferences(retainedSentinels),
			CountAlive(layoutRefs),
			CountAlive(sentinelChildRefs),
			CountAlive(sentinelPayloadRefs),
			CountAlive(sentinelPayloadBufferRefs),
			CountAlive(siblingChildRefs),
			CountAlive(siblingPayloadRefs),
			CountAlive(siblingPayloadBufferRefs),
			heapBefore,
			heapAfter);

		GC.KeepAlive(retainedSentinels);
		return result;
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	static void CreateClearedFlexLayoutScenario(
		int layoutIndex,
		RemovalMode removalMode,
		bool retainSentinel,
		List<Label> retainedSentinels,
		List<WeakReference<FlexLayout>> layoutRefs,
		List<WeakReference<Label>> sentinelChildRefs,
		List<WeakReference<Payload>> sentinelPayloadRefs,
		List<WeakReference<byte[]>> sentinelPayloadBufferRefs,
		List<WeakReference<Label>> siblingChildRefs,
		List<WeakReference<Payload>> siblingPayloadRefs,
		List<WeakReference<byte[]>> siblingPayloadBufferRefs)
	{
		var host = new Grid();
		var layout = new FlexLayout
		{
			Direction = FlexDirection.Row,
			Wrap = FlexWrap.Wrap
		};

		host.Add(layout);

		var children = new List<Label>(ChildrenPerLayout);

		for (var childIndex = 0; childIndex < ChildrenPerLayout; childIndex++)
		{
			var payloadIndex = layoutIndex * ChildrenPerLayout + childIndex;
			var payload = new Payload(payloadIndex, PayloadBytes);
			var child = new Label
			{
				Text = $"Tile {layoutIndex}:{childIndex}",
				BindingContext = payload,
				WidthRequest = 160,
				HeightRequest = 44
			};

			FlexLayout.SetGrow(child, 1);
			layout.Add(child);
			children.Add(child);

			if (childIndex == 0)
			{
				sentinelChildRefs.Add(new WeakReference<Label>(child));
				sentinelPayloadRefs.Add(new WeakReference<Payload>(payload));
				sentinelPayloadBufferRefs.Add(new WeakReference<byte[]>(payload.Buffer));
			}
			else
			{
				siblingChildRefs.Add(new WeakReference<Label>(child));
				siblingPayloadRefs.Add(new WeakReference<Payload>(payload));
				siblingPayloadBufferRefs.Add(new WeakReference<byte[]>(payload.Buffer));
			}
		}

		var sentinel = children[0];

		if (removalMode == RemovalMode.Clear)
		{
			layout.Clear();
		}
		else
		{
			for (var index = layout.Count - 1; index >= 0; index--)
				layout.RemoveAt(index);
		}

		if (sentinel.Parent is not null)
			throw new InvalidOperationException("Removed sentinel child still had a logical parent after cleanup.");

		if (retainSentinel)
			retainedSentinels.Add(sentinel);

		layoutRefs.Add(new WeakReference<FlexLayout>(layout));
	}

	static int CountStaleFlexItemReferences(IEnumerable<Label> sentinels)
	{
		var count = 0;
		foreach (var sentinel in sentinels)
		{
			if (sentinel.GetValue(FlexItemProperty) is not null)
				count++;
		}

		return count;
	}

	static async Task WaitForCollectionAsync()
	{
		for (var i = 0; i < 6; i++)
		{
			ForceFullGc();
			await Task.Delay(50);
		}
	}

	static void ForceFullGc()
	{
		for (var i = 0; i < 4; i++)
		{
			GC.Collect();
			GC.WaitForPendingFinalizers();
			GC.Collect();
		}
	}

	static int CountAlive<T>(IEnumerable<WeakReference<T>> references)
		where T : class
	{
		var count = 0;
		foreach (var reference in references)
		{
			if (reference.TryGetTarget(out _))
				count++;
		}

		return count;
	}

	enum RemovalMode
	{
		Clear,
		RemoveAt
	}

	readonly record struct ScenarioResult(
		string Name,
		RemovalMode RemovalMode,
		bool RetainSentinel,
		int RetainedSentinels,
		int StaleFlexItemReferences,
		int LayoutSurvivors,
		int SentinelChildSurvivors,
		int SentinelPayloadSurvivors,
		int SentinelPayloadBufferSurvivors,
		int SiblingChildSurvivors,
		int SiblingPayloadSurvivors,
		int SiblingPayloadBufferSurvivors,
		long HeapBeforeBytes,
		long HeapAfterBytes)
	{
		public long HeapDeltaBytes => HeapAfterBytes - HeapBeforeBytes;
		public double RetainedSiblingPayloadMiB => SiblingPayloadBufferSurvivors * PayloadBytes / 1024d / 1024d;
		public double RetainedSentinelPayloadMiB => SentinelPayloadBufferSurvivors * PayloadBytes / 1024d / 1024d;

		public void AppendTo(StringBuilder builder)
		{
			builder.AppendLine(Name);
			builder.AppendLine($"  Removal mode: {RemovalMode}");
			builder.AppendLine($"  Retain one removed child per FlexLayout: {RetainSentinel}");
			builder.AppendLine($"  Retained sentinel children: {RetainedSentinels}/{SentinelChildren}");
			builder.AppendLine($"  Stale FlexItem references on retained sentinels: {StaleFlexItemReferences}/{SentinelChildren}");
			builder.AppendLine($"  FlexLayout survivors: {LayoutSurvivors}/{LayoutCount}");
			builder.AppendLine($"  Sentinel child survivors: {SentinelChildSurvivors}/{SentinelChildren}");
			builder.AppendLine($"  Sentinel payload survivors: {SentinelPayloadSurvivors}/{SentinelChildren}");
			builder.AppendLine($"  Sentinel payload buffer survivors: {SentinelPayloadBufferSurvivors}/{SentinelChildren}");
			builder.AppendLine($"  Sibling child survivors: {SiblingChildSurvivors}/{SiblingChildren}");
			builder.AppendLine($"  Sibling payload survivors: {SiblingPayloadSurvivors}/{SiblingChildren}");
			builder.AppendLine($"  Sibling payload buffer survivors: {SiblingPayloadBufferSurvivors}/{SiblingChildren}");
			builder.AppendLine($"  Retained sentinel payload estimate: {RetainedSentinelPayloadMiB:F1} MiB");
			builder.AppendLine($"  Retained sibling payload estimate: {RetainedSiblingPayloadMiB:F1} MiB");
			builder.AppendLine($"  Managed heap before: {HeapBeforeBytes:N0} bytes");
			builder.AppendLine($"  Managed heap after: {HeapAfterBytes:N0} bytes");
			builder.AppendLine($"  Managed heap delta: {HeapDeltaBytes:N0} bytes");
		}
	}

	readonly record struct ReproResult(ScenarioResult Baseline, ScenarioResult Control, ScenarioResult Current, bool Proven)
	{
		public string ToReport()
		{
			var builder = new StringBuilder();
			builder.AppendLine("FlexLayout Clear FlexItem retention repro");
			builder.AppendLine($"Result: {(Proven ? "PROVEN" : "NOT PROVEN")}");
			builder.AppendLine();
			builder.AppendLine("Trigger:");
			builder.AppendLine("  A FlexLayout has generated Flex.Item nodes for children, Clear() is called, and app code or platform cleanup temporarily keeps one removed child alive.");
			builder.AppendLine("  FlexLayout.OnClear() runs after Layout.Clear() has removed the children, so ClearLayout() cannot clear the private FlexItem attached property on those removed children.");
			builder.AppendLine("  The retained child keeps its stale Flex.Item, whose Parent is the old root item. That old root still contains sibling Flex.Items, and their SelfSizing delegates capture sibling child views.");
			builder.AppendLine();
			Baseline.AppendTo(builder);
			builder.AppendLine();
			Control.AppendTo(builder);
			builder.AppendLine();
			Current.AppendTo(builder);
			return builder.ToString();
		}
	}
}
