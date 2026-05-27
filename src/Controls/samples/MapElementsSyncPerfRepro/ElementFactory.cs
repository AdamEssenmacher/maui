using Microsoft.Maui.Controls.Maps;
using Microsoft.Maui.Devices.Sensors;
using Microsoft.Maui.Maps;

namespace MapElementsSyncPerfRepro;

internal static class ElementFactory
{
	static readonly Color[] s_strokeColors =
	{
		Color.FromArgb("#D73027"),
		Color.FromArgb("#4575B4"),
		Color.FromArgb("#1A9850"),
		Color.FromArgb("#984EA3")
	};

	static readonly Color[] s_fillColors =
	{
		Color.FromArgb("#33D73027"),
		Color.FromArgb("#334575B4"),
		Color.FromArgb("#331A9850"),
		Color.FromArgb("#33984EA3")
	};

	public static MapElement CreateElement(MapElementKind kind, int index, int seed)
	{
		var center = CreateLocation(index, seed);

		return kind switch
		{
			MapElementKind.Circle => CreateCircle(center, index),
			MapElementKind.ShortPolyline => CreateShortPolyline(center, index),
			_ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
		};
	}

	public static MapSpan CreateMapSpan()
	{
		return MapSpan.FromCenterAndRadius(
			new Location(47.6062, -122.3321),
			Distance.FromMiles(12));
	}

	static Circle CreateCircle(Location center, int index)
	{
		var colorIndex = index % s_strokeColors.Length;

		return new Circle
		{
			Center = center,
			Radius = Distance.FromMeters(18 + (index % 5) * 6),
			StrokeColor = s_strokeColors[colorIndex],
			StrokeWidth = 2,
			FillColor = s_fillColors[colorIndex]
		};
	}

	static Polyline CreateShortPolyline(Location center, int index)
	{
		var colorIndex = index % s_strokeColors.Length;
		var offset = 0.00035 + (index % 7) * 0.00003;
		var polyline = new Polyline
		{
			StrokeColor = s_strokeColors[colorIndex],
			StrokeWidth = 3
		};

		polyline.Add(new Location(center.Latitude - offset, center.Longitude - offset));
		polyline.Add(new Location(center.Latitude + offset, center.Longitude + offset));

		return polyline;
	}

	static Location CreateLocation(int index, int seed)
	{
		var row = index / 50;
		var column = index % 50;
		var seedOffset = Math.Abs(seed % 997) * 0.000001;
		var latitude = 47.5300 + row * 0.0028 + seedOffset;
		var longitude = -122.4300 + column * 0.0038 - seedOffset;

		return new Location(latitude, longitude);
	}
}
