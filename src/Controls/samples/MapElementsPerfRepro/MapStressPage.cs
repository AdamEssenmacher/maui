using Microsoft.Maui.Controls.Maps;
using Microsoft.Maui.Devices.Sensors;
using Microsoft.Maui.Maps;
using ControlsMap = Microsoft.Maui.Controls.Maps.Map;

namespace MapElementsPerfRepro;

public sealed class MapStressPage : ContentPage
{
	readonly ReproSession _session;
	readonly ControlsMap _map;
	readonly Label _statusLabel;
	readonly Label _resultLabel;
	bool _started;
	bool _heartbeatRunning;

	public MapStressPage()
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
		StartHeartbeat();
		_session.Start();
		Dispatcher.Dispatch(async () => await RunStressAsync());
	}

	protected override void OnDisappearing()
	{
		_heartbeatRunning = false;
		base.OnDisappearing();
	}

	void StartHeartbeat()
	{
		_heartbeatRunning = true;
		Dispatcher.StartTimer(TimeSpan.FromMilliseconds(250), () =>
		{
			if (!_heartbeatRunning)
				return false;

			_session.MarkHeartbeat();
			return true;
		});
	}

	async Task RunStressAsync()
	{
		try
		{
			_statusLabel.Text = "Preparing map.";
			await Task.Delay(500);

			Render();
			_statusLabel.Text = $"Observing map after add loop for {_session.Options.PostRenderObservationSeconds}s.";
			await Task.Delay(TimeSpan.FromSeconds(_session.Options.PostRenderObservationSeconds));
			_session.Complete();

			var result = await _session.Completion;
			_statusLabel.Text = "Completed.";
			_resultLabel.Text = result.ToDisplayText();
		}
		catch (Exception exception)
		{
			_session.Fail(exception);
			var result = await _session.Completion;
			_statusLabel.Text = "Failed.";
			_resultLabel.Text = result.ToDisplayText();
		}
	}

	void Render()
	{
		var options = _session.Options;
		var northWest = new Location(48.60660019765632, -121.6898628046794);
		var southWest = new Location(34.932324583554866, -115.69598307100047);

		_map.MoveToRegion(MapSpan.FromCenterAndRadius(
			new Location(
				(northWest.Latitude + southWest.Latitude) / 2,
				(northWest.Longitude + southWest.Longitude) / 2),
			Distance.FromMiles(600.0)));

		var random = new Random(options.Seed);
		_statusLabel.Text = $"Running {options.Name}: {options.PolylineCount} x {options.PointsPerPolyline}.";

		for (var polylineIndex = 0; polylineIndex < options.PolylineCount; polylineIndex++)
		{
			northWest.Longitude += 0.8;
			southWest.Longitude += 0.8;

			var polyline = CreatePolyline(northWest, southWest, options.PointsPerPolyline, random);
			var oneBasedPolylineIndex = polylineIndex + 1;
			_session.MarkGeneratedPolyline(oneBasedPolylineIndex);

			if (options.AddToMap)
			{
				_map.MapElements.Add(polyline);
				_session.MarkAddedPolyline(oneBasedPolylineIndex);
			}

			if (oneBasedPolylineIndex == 1 ||
				oneBasedPolylineIndex == options.PolylineCount ||
				oneBasedPolylineIndex % options.ProgressLogInterval == 0)
			{
				_statusLabel.Text = options.AddToMap
					? $"Added {oneBasedPolylineIndex}/{options.PolylineCount} polylines."
					: $"Generated {oneBasedPolylineIndex}/{options.PolylineCount} polylines.";
			}
		}
	}

	static Polyline CreatePolyline(Location northWest, Location southWest, int pointsPerPolyline, Random random)
	{
		var polyline = new Polyline
		{
			StrokeColor = Colors.Red,
			StrokeWidth = 3
		};

		for (var step = 0; step < pointsPerPolyline; step++)
		{
			var latitude = northWest.Latitude - (northWest.Latitude - southWest.Latitude) * step / pointsPerPolyline;
			var longitudeJitter = random.NextDouble() / 2;
			var longitude = northWest.Longitude - (northWest.Longitude - southWest.Longitude) * step / pointsPerPolyline + longitudeJitter;

			polyline.Add(new Location(latitude, longitude));
		}

		return polyline;
	}
}
