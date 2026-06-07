using Microsoft.Maui.Controls.Shapes;
using Microsoft.Maui.Graphics;
using ShapePath = Microsoft.Maui.Controls.Shapes.Path;

namespace GeometryGroupLeakRepro;

internal static class GeometryCardFactory
{
	static readonly Geometry[] SharedFragments = Enumerable.Range(0, 240)
		.Select(CreateFragment)
		.ToArray();

	public static TrackedGeometryVisual CreateTrackedPath(ReproOptions options, LeakPayloadViewModel payload, int cycle, int cardIndex)
	{
		var group = new GeometryGroup
		{
			FillRule = FillRule.Nonzero
		};

		AddTransientFragments(group, options, cycle, cardIndex);

		if (options.RemoveFragmentsIndividually)
			RemoveAllChildrenIndividually(group);
		else
			group.Children.Clear();

		AddVisibleCardGeometry(group, cardIndex);

		var path = new ShapePath
		{
			Data = group,
			BindingContext = payload,
			WidthRequest = 56,
			HeightRequest = 56,
			Aspect = Stretch.Uniform,
			Fill = Color.FromArgb("#DCEAFE"),
			Stroke = Color.FromArgb("#194A8D"),
			StrokeThickness = 2,
			BackgroundColor = Colors.Transparent
		};

		return new TrackedGeometryVisual(path, group);
	}

	static void AddTransientFragments(GeometryGroup group, ReproOptions options, int cycle, int cardIndex)
	{
		for (var i = 0; i < options.SharedFragmentsPerCard; i++)
		{
			var fragmentIndex = Math.Abs((cycle * 31) + (cardIndex * 17) + i);
			var fragment = options.UsesSharedFragments
				? SharedFragments[fragmentIndex % SharedFragments.Length]
				: CreateFragment(fragmentIndex);

			group.Children.Add(fragment);
		}
	}

	static void RemoveAllChildrenIndividually(GeometryGroup group)
	{
		while (group.Children.Count > 0)
			group.Children.RemoveAt(group.Children.Count - 1);
	}

	static void AddVisibleCardGeometry(GeometryGroup group, int cardIndex)
	{
		var lane = cardIndex % 4;
		var y = 9 + (lane * 3);

		group.Children.Add(new RectangleGeometry(new Rect(6, 14, 44, 30)));
		group.Children.Add(new RectangleGeometry(new Rect(13, 7, 30, 12)));
		group.Children.Add(new EllipseGeometry(new Point(19, y + 14), 4, 4));
		group.Children.Add(new EllipseGeometry(new Point(31, y + 14), 4, 4));
		group.Children.Add(new RectangleGeometry(new Rect(15, 34, 26, 4)));
	}

	static Geometry CreateFragment(int seed)
	{
		var offset = seed % 11;

		return (seed % 3) switch
		{
			0 => new RectangleGeometry(new Rect(3 + offset, 8 + (offset % 4), 38, 18)),
			1 => new EllipseGeometry(new Point(18 + offset, 19 + (offset % 5)), 6 + (offset % 3), 5 + (offset % 4)),
			_ => new LineGeometry(new Point(6 + offset, 12 + (offset % 7)), new Point(46 - offset, 42 - (offset % 5)))
		};
	}
}
