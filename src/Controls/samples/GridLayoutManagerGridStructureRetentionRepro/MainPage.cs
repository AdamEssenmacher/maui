using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Maui;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Graphics;
using Microsoft.Maui.Layouts;

namespace GridLayoutManagerGridStructureRetentionRepro;

public sealed class MainPage : ContentPage
{
	const int GridCount = 64;
	const int ChildrenPerGrid = 4;
	const int RemovedChildren = GridCount * ChildrenPerGrid;
	const int PayloadBytes = 1024 * 1024;
	const int SurvivorTolerance = 2;

	static readonly FieldInfo LayoutManagerField =
		typeof(Layout).GetField("_layoutManager", BindingFlags.Instance | BindingFlags.NonPublic)
			?? throw new MissingFieldException(typeof(Layout).FullName, "_layoutManager");

	static readonly FieldInfo GridStructureField =
		typeof(GridLayoutManager).GetField("_gridStructure", BindingFlags.Instance | BindingFlags.NonPublic)
			?? throw new MissingFieldException(typeof(GridLayoutManager).FullName, "_gridStructure");

	static readonly Type GridStructureType =
		typeof(GridLayoutManager).GetNestedType("GridStructure", BindingFlags.NonPublic)
			?? throw new MissingMemberException(typeof(GridLayoutManager).FullName, "GridStructure");

	static readonly FieldInfo ChildrenToLayOutField =
		GridStructureType.GetField("_childrenToLayOut", BindingFlags.Instance | BindingFlags.NonPublic)
			?? throw new MissingFieldException(GridStructureType.FullName, "_childrenToLayOut");

	readonly string? _resultsPath;
	readonly Label _status;
	bool _started;

	public MainPage(string? resultsPath)
	{
		_resultsPath = resultsPath;
		Title = "GridLayoutManager GridStructure Retention";

		_status = new Label
		{
			Text = "Running GridLayoutManager GridStructure retention repro...",
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
				? "PROVEN: Grid retained removed children through cached GridStructure."
				: "NOT PROVEN: removed Grid children did not remain alive.";

			if (!string.IsNullOrWhiteSpace(_resultsPath))
				File.WriteAllText(_resultsPath, report);

			await Task.Delay(250);
			Environment.Exit(result.Proven ? 0 : 2);
		}
		catch (Exception ex)
		{
			var report = "GridLayoutManager GridStructure retention repro failed." + Environment.NewLine + ex;
			_status.Text = report;

			if (!string.IsNullOrWhiteSpace(_resultsPath))
				File.WriteAllText(_resultsPath, report);

			await Task.Delay(250);
			Environment.Exit(3);
		}
	}

	static async Task<ReproResult> RunReproAsync()
	{
		var baseline = await RunScenarioAsync("Baseline: no prior Grid measure", measureBeforeClear: false, clearGridStructure: false);
		var control = await RunScenarioAsync("Control: prior Grid measure plus explicit GridStructure clear", measureBeforeClear: true, clearGridStructure: true);
		var current = await RunScenarioAsync("Current MAUI behavior: prior Grid measure, then Children.Clear()", measureBeforeClear: true, clearGridStructure: false);

		var baselineCollected = baseline.GridSurvivors >= GridCount - SurvivorTolerance
			&& baseline.StaleGridStructureChildReferences <= SurvivorTolerance
			&& baseline.ChildSurvivors <= SurvivorTolerance
			&& baseline.PayloadSurvivors <= SurvivorTolerance
			&& baseline.PayloadBufferSurvivors <= SurvivorTolerance;

		var controlCollected = control.GridSurvivors >= GridCount - SurvivorTolerance
			&& control.StaleGridStructureChildReferences <= SurvivorTolerance
			&& control.ChildSurvivors <= SurvivorTolerance
			&& control.PayloadSurvivors <= SurvivorTolerance
			&& control.PayloadBufferSurvivors <= SurvivorTolerance;

		var currentRetained = current.GridSurvivors >= GridCount - SurvivorTolerance
			&& current.StaleGridStructureChildReferences >= RemovedChildren - SurvivorTolerance
			&& current.ChildSurvivors >= RemovedChildren - SurvivorTolerance
			&& current.PayloadSurvivors >= RemovedChildren - SurvivorTolerance
			&& current.PayloadBufferSurvivors >= RemovedChildren - SurvivorTolerance;

		return new ReproResult(baseline, control, current, baselineCollected && controlCollected && currentRetained);
	}

	static async Task<ScenarioResult> RunScenarioAsync(string name, bool measureBeforeClear, bool clearGridStructure)
	{
		var retainedGrids = new List<Grid>(GridCount);
		var gridRefs = new List<WeakReference<Grid>>(GridCount);
		var childRefs = new List<WeakReference<Label>>(RemovedChildren);
		var payloadRefs = new List<WeakReference<Payload>>(RemovedChildren);
		var payloadBufferRefs = new List<WeakReference<byte[]>>(RemovedChildren);

		ForceFullGc();
		var heapBefore = GC.GetTotalMemory(forceFullCollection: true);

		for (var gridIndex = 0; gridIndex < GridCount; gridIndex++)
		{
			CreateGridScenario(
				gridIndex,
				measureBeforeClear,
				clearGridStructure,
				retainedGrids,
				gridRefs,
				childRefs,
				payloadRefs,
				payloadBufferRefs);

			if (gridIndex % 8 == 0)
				await Task.Yield();
		}

		await WaitForCollectionAsync();
		var heapAfter = GC.GetTotalMemory(forceFullCollection: true);

		var result = new ScenarioResult(
			name,
			measureBeforeClear,
			clearGridStructure,
			retainedGrids.Count,
			CountStaleGridStructureChildReferences(retainedGrids),
			CountAlive(gridRefs),
			CountAlive(childRefs),
			CountAlive(payloadRefs),
			CountAlive(payloadBufferRefs),
			heapBefore,
			heapAfter);

		GC.KeepAlive(retainedGrids);
		return result;
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	static void CreateGridScenario(
		int gridIndex,
		bool measureBeforeClear,
		bool clearGridStructure,
		List<Grid> retainedGrids,
		List<WeakReference<Grid>> gridRefs,
		List<WeakReference<Label>> childRefs,
		List<WeakReference<Payload>> payloadRefs,
		List<WeakReference<byte[]>> payloadBufferRefs)
	{
		var grid = new Grid
		{
			WidthRequest = 480,
			HeightRequest = 720,
			ColumnSpacing = 8,
			RowSpacing = 8
		};

		grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
		grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
		grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
		grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

		for (var childIndex = 0; childIndex < ChildrenPerGrid; childIndex++)
		{
			var payloadIndex = gridIndex * ChildrenPerGrid + childIndex;
			var payload = new Payload(payloadIndex, PayloadBytes);
			var child = new Label
			{
				Text = $"Tile {gridIndex}:{childIndex}",
				BindingContext = payload,
				HeightRequest = 96,
				WidthRequest = 180,
				HorizontalOptions = LayoutOptions.Fill,
				VerticalOptions = LayoutOptions.Fill
			};

			Grid.SetRow(child, childIndex / 2);
			Grid.SetColumn(child, childIndex % 2);
			grid.Add(child);

			childRefs.Add(new WeakReference<Label>(child));
			payloadRefs.Add(new WeakReference<Payload>(payload));
			payloadBufferRefs.Add(new WeakReference<byte[]>(payload.Buffer));
		}

		if (measureBeforeClear)
			_ = grid.CrossPlatformMeasure(480, 720);

		grid.Clear();

		if (clearGridStructure)
			ClearGridStructure(grid);

		if (grid.Count != 0)
			throw new InvalidOperationException("Grid still had children after Clear().");

		retainedGrids.Add(grid);
		gridRefs.Add(new WeakReference<Grid>(grid));
	}

	static void ClearGridStructure(Grid grid)
	{
		if (LayoutManagerField.GetValue(grid) is GridLayoutManager gridLayoutManager)
			GridStructureField.SetValue(gridLayoutManager, null);
	}

	static int CountStaleGridStructureChildReferences(IEnumerable<Grid> grids)
	{
		var count = 0;
		foreach (var grid in grids)
		{
			if (LayoutManagerField.GetValue(grid) is not GridLayoutManager gridLayoutManager)
				continue;

			var gridStructure = GridStructureField.GetValue(gridLayoutManager);
			if (gridStructure is null)
				continue;

			if (ChildrenToLayOutField.GetValue(gridStructure) is IView[] children)
			{
				foreach (var child in children)
				{
					if (child is not null)
						count++;
				}
			}
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
		bool ClearGridStructure,
		int RetainedGrids,
		int StaleGridStructureChildReferences,
		int GridSurvivors,
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
			builder.AppendLine($"  Prior Grid measure: {MeasureBeforeClear}");
			builder.AppendLine($"  Explicitly clear cached GridStructure: {ClearGridStructure}");
			builder.AppendLine($"  Retained Grids: {RetainedGrids}/{GridCount}");
			builder.AppendLine($"  Grid survivors: {GridSurvivors}/{GridCount}");
			builder.AppendLine($"  Stale GridStructure child references: {StaleGridStructureChildReferences}/{RemovedChildren}");
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
			builder.AppendLine("GridLayoutManager GridStructure retention repro");
			builder.AppendLine($"Result: {(Proven ? "PROVEN" : "NOT PROVEN")}");
			builder.AppendLine();
			builder.AppendLine("Trigger:");
			builder.AppendLine("  A Grid is measured, causing GridLayoutManager to store a GridStructure in _gridStructure.");
			builder.AppendLine("  GridStructure stores the measured child views in its _childrenToLayOut array.");
			builder.AppendLine("  When the Grid children are later cleared, the cached GridStructure is not cleared unless another measure replaces it.");
			builder.AppendLine("  A live Grid can therefore retain removed child views, their BindingContexts, and payloads through _layoutManager -> _gridStructure -> _childrenToLayOut.");
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
