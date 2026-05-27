using Microsoft.Maui.Controls.Maps;
using Microsoft.Maui.Devices.Sensors;
using Microsoft.Maui.Maps;
using System.Diagnostics;
using ControlsMap = Microsoft.Maui.Controls.Maps.Map;

namespace MapGeopathAppendRepro;

public sealed class MapMutationPage : ContentPage
{
	readonly ReproSession _session;
	readonly ControlsMap _map;
	readonly Label _statusLabel;
	readonly Label _resultLabel;
	bool _started;

	public MapMutationPage()
	{
		_session = ReproSession.Current ?? throw new InvalidOperationException("No active repro session.");
		Title = _session.Options.Name;
		BackgroundColor = Colors.White;

		_map = new ControlsMap
		{
			IsShowingUser = false,
			IsTrafficEnabled = false
		};

		_statusLabel = new Label
		{
			Text = "Waiting to start.",
			Margin = new Thickness(12),
			Padding = new Thickness(8),
			FontSize = 13,
			TextColor = Colors.White,
			BackgroundColor = Color.FromArgb("#B0000000")
		};

		_resultLabel = new Label
		{
			Text = string.Empty,
			Margin = new Thickness(12),
			Padding = new Thickness(8),
			FontSize = 12,
			TextColor = Colors.White,
			BackgroundColor = Color.FromArgb("#B0000000")
		};

		var overlay = new Grid
		{
			RowDefinitions =
			{
				new RowDefinition(GridLength.Auto),
				new RowDefinition(GridLength.Star),
				new RowDefinition(GridLength.Auto)
			}
		};

		overlay.Add(_statusLabel, 0, 0);
		overlay.Add(_resultLabel, 0, 2);

		Content = new Grid
		{
			Children =
			{
				_map,
				overlay
			}
		};
	}

	protected override void OnAppearing()
	{
		base.OnAppearing();

		if (_started)
			return;

		_started = true;
		_session.Start();
		Dispatcher.Dispatch(async () => await RunAsync());
	}

	async Task RunAsync()
	{
		try
		{
			_statusLabel.Text = "Preparing map.";
			await WaitForMapReadyAsync();
			MoveToRouteRegion();
			await Task.Delay(500);

			var result = _session.Options.Scenario == ReproScenario.FreshInstanceControl
				? await RunFreshInstanceControlAsync()
				: await RunRetainedMutationAsync();

			_statusLabel.Text = "Completed.";
			_resultLabel.Text = result.ToDisplayText();
			_session.Complete(result);
		}
		catch (Exception exception)
		{
			_session.Fail(exception);
			var result = await _session.Completion;
			_statusLabel.Text = "Failed.";
			_resultLabel.Text = result.ToDisplayText();
		}
	}

	async Task<ReproResult> RunFreshInstanceControlAsync()
	{
		if (!MapDiagnostics.SupportsRetainedOptionsInspection)
			return CreateUnsupportedResult();

		var options = _session.Options;
		_statusLabel.Text = $"Building fresh polyline with {options.LogicalPointCount} points.";

		var runtimeBefore = RuntimeMetrics.Capture();
		var initialRenderStopwatch = Stopwatch.StartNew();
		var polyline = CreatePolyline(options.LogicalPointCount);
		_map.MapElements.Clear();
		_map.MapElements.Add(polyline);
		await WaitForMapElementIdAsync(polyline, expectMapElementId: true);
		initialRenderStopwatch.Stop();

		var retainedOptionsPointCount = MapDiagnostics.GetRetainedOptionsPointCount(polyline);
		var nativePolylinePointCount = MapDiagnostics.GetCurrentNativePolylinePointCount(_map, polyline);
		var countsAreIdempotent = CountsMatchLogical(options.LogicalPointCount, retainedOptionsPointCount, nativePolylinePointCount);
		var runtimeAfter = RuntimeMetrics.Capture();
		var impact = RuntimeImpact.Create(
			runtimeBefore,
			runtimeAfter,
			initialRenderStopwatch.Elapsed,
			TimeSpan.Zero,
			TimeSpan.Zero);

		return _session.CreateResult(
			countsAreIdempotent ? ReproStatus.Completed : ReproStatus.Failed,
			impact,
			null,
			null,
			retainedOptionsPointCount,
			nativePolylinePointCount,
			countsAreIdempotent
				? "Fresh instance control matched the logical route point count."
				: "Fresh instance control did not match the logical route point count.");
	}

	async Task<ReproResult> RunRetainedMutationAsync()
	{
		if (!MapDiagnostics.SupportsRetainedOptionsInspection)
			return CreateUnsupportedResult();

		var options = _session.Options;
		_statusLabel.Text = $"Rendering initial route with {options.InitialPointCount} points.";

		var runtimeBefore = RuntimeMetrics.Capture();
		var initialRenderStopwatch = Stopwatch.StartNew();
		var polyline = CreatePolyline(options.InitialPointCount);
		_map.MapElements.Clear();
		_map.MapElements.Add(polyline);
		await WaitForMapElementIdAsync(polyline, expectMapElementId: true);
		initialRenderStopwatch.Stop();

		var retainedOptionsPointCountBeforeMutation = MapDiagnostics.GetRetainedOptionsPointCount(polyline);

		var offMapMutationStopwatch = Stopwatch.StartNew();
		_statusLabel.Text = "Removing route but keeping the same Polyline instance alive.";
		_map.MapElements.Remove(polyline);
		await WaitForMapElementIdAsync(polyline, expectMapElementId: false);
		await Task.Delay(100);

		_statusLabel.Text = $"Appending {options.AppendedPointCount} off-map points through {options.MutationApiName}.";
		for (var pointIndex = options.InitialPointCount; pointIndex < options.LogicalPointCount; pointIndex++)
		{
			AppendRoutePoint(polyline, CreateRoutePoint(pointIndex));

			if (options.StepDelayMilliseconds > 0)
				await Task.Delay(options.StepDelayMilliseconds);

			if ((pointIndex + 1) == options.LogicalPointCount || (pointIndex + 1) % 20 == 0)
				_statusLabel.Text = $"Logical route points: {pointIndex + 1}/{options.LogicalPointCount}.";
		}

		var retainedOptionsPointCountAfterMutation = MapDiagnostics.GetRetainedOptionsPointCount(polyline);
		offMapMutationStopwatch.Stop();

		var reAddStopwatch = Stopwatch.StartNew();
		_statusLabel.Text = "Re-adding the same Polyline instance.";
		_map.MapElements.Add(polyline);
		await WaitForMapElementIdAsync(polyline, expectMapElementId: true);

		var retainedOptionsPointCountAfterReAdd = MapDiagnostics.GetRetainedOptionsPointCount(polyline);
		var nativePolylinePointCountAfterReAdd = MapDiagnostics.GetCurrentNativePolylinePointCount(_map, polyline);
		reAddStopwatch.Stop();

		var runtimeAfter = RuntimeMetrics.Capture();
		var impact = RuntimeImpact.Create(
			runtimeBefore,
			runtimeAfter,
			initialRenderStopwatch.Elapsed,
			offMapMutationStopwatch.Elapsed,
			reAddStopwatch.Elapsed);
		var observedCount = nativePolylinePointCountAfterReAdd ?? retainedOptionsPointCountAfterReAdd ?? retainedOptionsPointCountAfterMutation;
		var reproduced = observedCount > options.LogicalPointCount;

		return _session.CreateResult(
			reproduced ? ReproStatus.Reproduced : ReproStatus.Failed,
			impact,
			retainedOptionsPointCountBeforeMutation,
			retainedOptionsPointCountAfterMutation,
			retainedOptionsPointCountAfterReAdd,
			nativePolylinePointCountAfterReAdd,
			reproduced
				? "Re-adding the same route object produced more native points than the logical MAUI route contains."
				: "The retained native options did not exceed the logical MAUI route point count.");
	}

	ReproResult CreateUnsupportedResult()
	{
		return _session.CreateResult(
			ReproStatus.NotSupported,
			RuntimeImpact.Empty,
			null,
			null,
			null,
			null,
			"This repro inspects Android PolylineOptions and native Android polylines.");
	}

	async Task WaitForMapReadyAsync()
	{
		for (var attempt = 0; attempt < 120; attempt++)
		{
			if (MapDiagnostics.IsPlatformMapReady(_map))
				return;

			await Task.Delay(50);
		}

		throw new TimeoutException("The platform map did not become ready.");
	}

	async Task WaitForMapElementIdAsync(Polyline polyline, bool expectMapElementId)
	{
		for (var attempt = 0; attempt < 120; attempt++)
		{
			var hasMapElementId = polyline.MapElementId is string;
			if (hasMapElementId == expectMapElementId)
				return;

			await Task.Delay(50);
		}

		throw new TimeoutException(expectMapElementId
			? "The polyline was not added to the native map."
			: "The polyline was not removed from the native map.");
	}

	void MoveToRouteRegion()
	{
		_map.MoveToRegion(MapSpan.FromCenterAndRadius(
			CreateRoutePoint(_session.Options.LogicalPointCount / 2),
			Distance.FromKilometers(9)));
	}

	void AppendRoutePoint(Polyline polyline, Location location)
	{
		if (_session.Options.UsesPolylineAddApi)
			polyline.Add(location);
		else
			polyline.Geopath.Add(location);
	}

	static bool CountsMatchLogical(int logicalPointCount, int? retainedOptionsPointCount, int? nativePolylinePointCount)
	{
		return retainedOptionsPointCount == logicalPointCount &&
			nativePolylinePointCount == logicalPointCount;
	}

	static Polyline CreatePolyline(int pointCount)
	{
		var polyline = new Polyline
		{
			StrokeColor = Colors.Red,
			StrokeWidth = 4
		};

		for (var pointIndex = 0; pointIndex < pointCount; pointIndex++)
		{
			polyline.Geopath.Add(CreateRoutePoint(pointIndex));
		}

		return polyline;
	}

	static Location CreateRoutePoint(int pointIndex)
	{
		var latitude = 47.6062 + pointIndex * 0.0018;
		var longitude = -122.3321 + Math.Sin(pointIndex * 0.32) * 0.018 + pointIndex * 0.00075;
		return new Location(latitude, longitude);
	}
}
