using Microsoft.Maui.Controls.Maps;
using Microsoft.Maui.Devices.Sensors;
using Microsoft.Maui.Maps;
using Microsoft.Maui.Maps.Handlers;

namespace MapPinLeakRepro;

public sealed class MapPinLeakPage : ContentPage
{
	readonly Microsoft.Maui.Controls.Maps.Map _map;
	readonly Label _statusLabel;
	bool _initialized;

	public MapPinLeakPage()
	{
		var session = ReproSession.Current ?? throw new InvalidOperationException("No active repro session.");
		var cycle = session.CurrentCycle;
		var payload = new PagePayload(cycle);

		Title = payload.Title;
		BindingContext = payload;

		_map = new Microsoft.Maui.Controls.Maps.Map(
			new MapSpan(new Location(47.6062, -122.3321), 0.08, 0.08))
		{
			IsScrollEnabled = false,
			IsZoomEnabled = false
		};

		_statusLabel = new Label
		{
			Text = "Waiting for Android MapHandler...",
			Margin = new Thickness(12),
			FontSize = 13,
			TextColor = Colors.White,
			BackgroundColor = Color.FromArgb("#8C000000")
		};
		Grid.SetRow(_statusLabel, 1);

		var layout = new Grid
		{
			RowDefinitions =
			{
				new RowDefinition(GridLength.Star),
				new RowDefinition(GridLength.Auto)
			}
		};

		layout.Add(_map);
		layout.Add(_statusLabel);
		Content = layout;
	}

	protected override void OnAppearing()
	{
		base.OnAppearing();

		if (_initialized)
			return;

		_initialized = true;
		Dispatcher.Dispatch(async () => await InitializeAsync());
	}

	async Task InitializeAsync()
	{
		var session = ReproSession.Current ?? throw new InvalidOperationException("No active repro session.");
		Exception? error = null;

		try
		{
			var handler = await WaitForMapHandlerAsync(TimeSpan.FromSeconds(20));
			var payload = BindingContext as PagePayload ?? throw new InvalidOperationException("Missing page payload.");
			session.Track(this, _map, handler, payload);

			var retainedPins = CreatePins(session.CurrentCycle, "retained", session.Options.PinsPerPage).ToArray();
			foreach (var pin in retainedPins)
				_map.Pins.Add(pin);

			session.RetainPins(retainedPins);
			await Task.Delay(100);

			if (session.Options.RemoveRetainedPinsBeforePageDisposal)
			{
				_map.Pins.Clear();
				await Task.Delay(100);

				var replacementPins = CreatePins(session.CurrentCycle, "replacement", Math.Max(1, session.Options.PinsPerPage / 2)).ToArray();
				foreach (var pin in replacementPins)
					_map.Pins.Add(pin);
			}

			_statusLabel.Text = $"{session.Options.Name}: cycle {session.CurrentCycle + 1}, retained pins {session.RetainedPinsCount}";
		}
		catch (Exception ex)
		{
			error = ex;
			_statusLabel.Text = ex.Message;
		}
		finally
		{
			session.CompleteCurrentPageReady(error);
		}
	}

	async Task<MapHandler> WaitForMapHandlerAsync(TimeSpan timeout)
	{
		var stopAt = DateTimeOffset.UtcNow + timeout;

		while (DateTimeOffset.UtcNow < stopAt)
		{
			if (_map.Handler is MapHandler mapHandler && mapHandler.Map is not null)
				return mapHandler;

			await Task.Delay(100);
		}

		throw new TimeoutException("Timed out waiting for the Android GoogleMap instance. Verify Google Play Services is available and the Maps API key in AndroidManifest.xml is valid for your environment.");
	}

	static IEnumerable<Pin> CreatePins(int cycle, string role, int count)
	{
		for (var i = 0; i < count; i++)
		{
			yield return new Pin
			{
				Label = $"{role} pin {cycle + 1:000}-{i + 1:000}",
				Address = "Retained by repro session",
				Type = PinType.Place,
				Location = new Location(
					47.6062 + cycle * 0.0002 + i * 0.0001,
					-122.3321 - cycle * 0.0002 - i * 0.0001)
			};
		}
	}
}
