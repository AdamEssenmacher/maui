using Microsoft.Maui.Controls.Maps;
using Microsoft.Maui.Devices.Sensors;
using Microsoft.Maui.Maps;
using ControlsMap = Microsoft.Maui.Controls.Maps.Map;

namespace MapPoolLeakRepro;

public sealed class MapLeakPage : ContentPage
{
	readonly ControlsMap _map;

	public MapLeakPage()
	{
		var session = ReproSession.Current ?? throw new InvalidOperationException("No active repro session.");
		var options = session.Options;
		var cycle = session.CurrentCycle;
		var payload = new LeakPayloadViewModel(cycle, options.PayloadBytesPerPage);
		var center = new Location(47.6205, -122.3493);

		Title = payload.Title;
		BindingContext = payload;

		_map = new ControlsMap(MapSpan.FromCenterAndRadius(center, Distance.FromMiles(1)))
		{
			BindingContext = payload,
			IsShowingUser = false,
			IsTrafficEnabled = false,
			IsScrollEnabled = true,
			IsZoomEnabled = true,
			HeightRequest = 420
		};

		if (options.AddMapElements)
			AddMapElements(_map, options.MapElementsPerPage, cycle);

		var mapElements = _map.MapElements.Cast<object>().ToArray();
		session.Track(this, _map, payload, mapElements);

		var footer = new Label
		{
			Text = $"{options.Name}: cycle {cycle + 1}, elements {mapElements.Length}, payload {options.PayloadMegabytesPerPage} MB",
			Margin = new Thickness(12),
			FontSize = 13,
			TextColor = Colors.White,
			BackgroundColor = Color.FromArgb("#8C000000")
		};
		Grid.SetRow(footer, 1);

		var layout = new Grid
		{
			RowDefinitions =
			{
				new RowDefinition(GridLength.Star),
				new RowDefinition(GridLength.Auto)
			},
		};

		layout.Add(_map);
		layout.Add(footer);
		Content = layout;
	}

	protected override void OnDisappearing()
	{
		if (ReproSession.Current?.Options.ClearElementsOnDisappear == true)
			_map.MapElements.Clear();

		base.OnDisappearing();
	}

	static void AddMapElements(ControlsMap map, int count, int cycle)
	{
		for (var i = 0; i < count; i++)
		{
			var offset = i * 0.00055;
			var lane = i % 10;
			var latitude = 47.615 + lane * 0.001 + cycle * 0.00003;
			var longitude = -122.355 + offset;
			var hue = (cycle * 23 + i * 11) % 360;

			map.MapElements.Add(new Circle
			{
				Center = new Location(latitude, longitude),
				Radius = Distance.FromMeters(45 + lane * 7),
				StrokeWidth = 2,
				StrokeColor = Color.FromHsla(hue / 360d, 0.75, 0.42, 0.85),
				FillColor = Color.FromHsla(hue / 360d, 0.75, 0.62, 0.22)
			});
		}
	}
}
