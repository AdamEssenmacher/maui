using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Maui;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Layouts;

namespace StackLayoutAndExpandGridRetentionRepro;

public sealed class MainPage : ContentPage
{
	const int StackCount = 60;
	const int ChildrenPerStack = 3;
	const int RemovedChildren = StackCount * ChildrenPerStack;
	const int PayloadBytes = 1024 * 1024;
	const int SurvivorTolerance = 2;

	static readonly FieldInfo LayoutManagerField =
		typeof(Layout).GetField("_layoutManager", BindingFlags.Instance | BindingFlags.NonPublic)
			?? throw new MissingFieldException(typeof(Layout).FullName, "_layoutManager");

	static readonly FieldInfo AndExpandManagerField =
		typeof(Microsoft.Maui.Controls.StackLayoutManager).GetField("_andExpandLayoutManager", BindingFlags.Instance | BindingFlags.NonPublic)
			?? throw new MissingFieldException(typeof(Microsoft.Maui.Controls.StackLayoutManager).FullName, "_andExpandLayoutManager");

	static readonly FieldInfo AndExpandGridLayoutField =
		typeof(AndExpandLayoutManager).GetField("_gridLayout", BindingFlags.Instance | BindingFlags.NonPublic)
			?? throw new MissingFieldException(typeof(AndExpandLayoutManager).FullName, "_gridLayout");

	readonly string? _resultsPath;
	readonly Label _status;
	bool _started;

	public MainPage(string? resultsPath)
	{
		_resultsPath = resultsPath;
		Title = "StackLayout AndExpand Grid Retention";

		_status = new Label
		{
			Text = "Running StackLayout AndExpand grid retention repro...",
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
				? "PROVEN: StackLayout retained removed children through the stale AndExpand grid."
				: "NOT PROVEN: removed StackLayout children did not remain alive.";

			if (!string.IsNullOrWhiteSpace(_resultsPath))
				File.WriteAllText(_resultsPath, report);

			await Task.Delay(250);
			Environment.Exit(result.Proven ? 0 : 2);
		}
		catch (Exception ex)
		{
			var report = "StackLayout AndExpand grid retention repro failed." + Environment.NewLine + ex;
			_status.Text = report;

			if (!string.IsNullOrWhiteSpace(_resultsPath))
				File.WriteAllText(_resultsPath, report);

			await Task.Delay(250);
			Environment.Exit(3);
		}
	}

	static async Task<ReproResult> RunReproAsync()
	{
		var baseline = await RunScenarioAsync("Baseline: no prior AndExpand measure", measureBeforeClear: false, clearAndExpandCache: false);
		var control = await RunScenarioAsync("Control: prior AndExpand measure plus explicit stale-grid clear", measureBeforeClear: true, clearAndExpandCache: true);
		var current = await RunScenarioAsync("Current MAUI behavior: prior AndExpand measure, then Children.Clear()", measureBeforeClear: true, clearAndExpandCache: false);

		var baselineCollected = baseline.StackSurvivors >= StackCount - SurvivorTolerance
			&& baseline.StaleGridChildReferences <= SurvivorTolerance
			&& baseline.ChildSurvivors <= SurvivorTolerance
			&& baseline.PayloadSurvivors <= SurvivorTolerance
			&& baseline.PayloadBufferSurvivors <= SurvivorTolerance;

		var controlCollected = control.StackSurvivors >= StackCount - SurvivorTolerance
			&& control.StaleGridChildReferences <= SurvivorTolerance
			&& control.ChildSurvivors <= SurvivorTolerance
			&& control.PayloadSurvivors <= SurvivorTolerance
			&& control.PayloadBufferSurvivors <= SurvivorTolerance;

		var currentRetained = current.StackSurvivors >= StackCount - SurvivorTolerance
			&& current.StaleGridChildReferences >= RemovedChildren - SurvivorTolerance
			&& current.ChildSurvivors >= RemovedChildren - SurvivorTolerance
			&& current.PayloadSurvivors >= RemovedChildren - SurvivorTolerance
			&& current.PayloadBufferSurvivors >= RemovedChildren - SurvivorTolerance;

		return new ReproResult(baseline, control, current, baselineCollected && controlCollected && currentRetained);
	}

	static async Task<ScenarioResult> RunScenarioAsync(string name, bool measureBeforeClear, bool clearAndExpandCache)
	{
		var retainedStacks = new List<StackLayout>(StackCount);
		var stackRefs = new List<WeakReference<StackLayout>>(StackCount);
		var childRefs = new List<WeakReference<Label>>(RemovedChildren);
		var payloadRefs = new List<WeakReference<Payload>>(RemovedChildren);
		var payloadBufferRefs = new List<WeakReference<byte[]>>(RemovedChildren);

		ForceFullGc();
		var heapBefore = GC.GetTotalMemory(forceFullCollection: true);

		for (var stackIndex = 0; stackIndex < StackCount; stackIndex++)
		{
			CreateStackScenario(
				stackIndex,
				measureBeforeClear,
				clearAndExpandCache,
				retainedStacks,
				stackRefs,
				childRefs,
				payloadRefs,
				payloadBufferRefs);

			if (stackIndex % 10 == 0)
				await Task.Yield();
		}

		await WaitForCollectionAsync();
		var heapAfter = GC.GetTotalMemory(forceFullCollection: true);

		var result = new ScenarioResult(
			name,
			measureBeforeClear,
			clearAndExpandCache,
			retainedStacks.Count,
			CountStaleGridChildReferences(retainedStacks),
			CountAlive(stackRefs),
			CountAlive(childRefs),
			CountAlive(payloadRefs),
			CountAlive(payloadBufferRefs),
			heapBefore,
			heapAfter);

		GC.KeepAlive(retainedStacks);
		return result;
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	static void CreateStackScenario(
		int stackIndex,
		bool measureBeforeClear,
		bool clearAndExpandCache,
		List<StackLayout> retainedStacks,
		List<WeakReference<StackLayout>> stackRefs,
		List<WeakReference<Label>> childRefs,
		List<WeakReference<Payload>> payloadRefs,
		List<WeakReference<byte[]>> payloadBufferRefs)
	{
		var stack = new StackLayout
		{
			Orientation = StackOrientation.Vertical,
			Spacing = 8,
			WidthRequest = 360,
			HeightRequest = 640
		};

		for (var childIndex = 0; childIndex < ChildrenPerStack; childIndex++)
		{
			var payloadIndex = stackIndex * ChildrenPerStack + childIndex;
			var payload = new Payload(payloadIndex, PayloadBytes);
			var child = new Label
			{
				Text = $"Row {stackIndex}:{childIndex}",
				BindingContext = payload,
				VerticalOptions = LayoutOptions.FillAndExpand,
				HeightRequest = 56
			};

			stack.Add(child);
			childRefs.Add(new WeakReference<Label>(child));
			payloadRefs.Add(new WeakReference<Payload>(payload));
			payloadBufferRefs.Add(new WeakReference<byte[]>(payload.Buffer));
		}

		if (measureBeforeClear)
			_ = stack.CrossPlatformMeasure(360, 640);

		stack.Clear();

		if (clearAndExpandCache)
			ClearAndExpandManager(stack);

		if (stack.Count != 0)
			throw new InvalidOperationException("StackLayout still had children after Clear().");

		retainedStacks.Add(stack);
		stackRefs.Add(new WeakReference<StackLayout>(stack));
	}

	static void ClearAndExpandManager(StackLayout stack)
	{
		if (LayoutManagerField.GetValue(stack) is not Microsoft.Maui.Controls.StackLayoutManager stackLayoutManager)
			return;

		AndExpandManagerField.SetValue(stackLayoutManager, null);
	}

	static int CountStaleGridChildReferences(IEnumerable<StackLayout> stacks)
	{
		var count = 0;
		foreach (var stack in stacks)
		{
			if (LayoutManagerField.GetValue(stack) is not Microsoft.Maui.Controls.StackLayoutManager stackLayoutManager)
				continue;

			var andExpandManager = AndExpandManagerField.GetValue(stackLayoutManager);
			if (andExpandManager is null)
				continue;

			if (AndExpandGridLayoutField.GetValue(andExpandManager) is IGridLayout grid)
				count += grid.Count;
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

	readonly record struct ScenarioResult(
		string Name,
		bool MeasureBeforeClear,
		bool ClearAndExpandCache,
		int RetainedStacks,
		int StaleGridChildReferences,
		int StackSurvivors,
		int ChildSurvivors,
		int PayloadSurvivors,
		int PayloadBufferSurvivors,
		long HeapBeforeBytes,
		long HeapAfterBytes)
	{
		public long HeapDeltaBytes => HeapAfterBytes - HeapBeforeBytes;
		public double RetainedPayloadMiB => PayloadBufferSurvivors * PayloadBytes / 1024d / 1024d;

		public void AppendTo(StringBuilder builder)
		{
			builder.AppendLine(Name);
			builder.AppendLine($"  Prior AndExpand measure: {MeasureBeforeClear}");
			builder.AppendLine($"  Explicitly clear stale AndExpand manager: {ClearAndExpandCache}");
			builder.AppendLine($"  Retained StackLayouts: {RetainedStacks}/{StackCount}");
			builder.AppendLine($"  StackLayout survivors: {StackSurvivors}/{StackCount}");
			builder.AppendLine($"  Stale private grid child references: {StaleGridChildReferences}/{RemovedChildren}");
			builder.AppendLine($"  Removed child survivors: {ChildSurvivors}/{RemovedChildren}");
			builder.AppendLine($"  Removed payload survivors: {PayloadSurvivors}/{RemovedChildren}");
			builder.AppendLine($"  Removed payload buffer survivors: {PayloadBufferSurvivors}/{RemovedChildren}");
			builder.AppendLine($"  Retained payload estimate: {RetainedPayloadMiB:F1} MiB");
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
			builder.AppendLine("StackLayout AndExpand grid retention repro");
			builder.AppendLine($"Result: {(Proven ? "PROVEN" : "NOT PROVEN")}");
			builder.AppendLine();
			builder.AppendLine("Trigger:");
			builder.AppendLine("  A StackLayout with FillAndExpand children is measured, forcing StackLayoutManager to create an AndExpandLayoutManager.");
			builder.AppendLine("  AndExpandLayoutManager builds a private Grid mirror and GridLayoutManager over the StackLayout children.");
			builder.AppendLine("  When the StackLayout children are later cleared, the private Grid mirror is not cleared unless another AndExpand measure rebuilds it.");
			builder.AppendLine("  A live StackLayout can therefore retain removed child views, their BindingContexts, and payloads through _layoutManager -> _andExpandLayoutManager -> _gridLayout.");
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
