using Microsoft.Maui.Controls.Shapes;
using Microsoft.Maui.Graphics;
using ShapePath = Microsoft.Maui.Controls.Shapes.Path;

namespace ShapeCollectionResetLeakRepro;

internal static class ShapeCardFactory
{
	static readonly PathSegment[] SharedPathSegments = Enumerable.Range(0, 360)
		.Select(CreatePathSegmentFragment)
		.ToArray();

	static readonly PathFigure[] SharedPathFigures = Enumerable.Range(0, 240)
		.Select(CreatePathFigureFragment)
		.ToArray();

	static readonly Geometry[] SharedGeometries = Enumerable.Range(0, 240)
		.Select(CreateGeometryFragment)
		.ToArray();

	public static TrackedShapeVisual CreateTrackedPath(ReproOptions options, LeakPayloadViewModel payload, int cycle, int cardIndex)
	{
		return options.Target switch
		{
			LeakTarget.PathFigureSegments => CreatePathFigureSegmentsPath(options, payload, cycle, cardIndex),
			LeakTarget.PathGeometryFigures => CreatePathGeometryFiguresPath(options, payload, cycle, cardIndex),
			LeakTarget.GeometryGroupChildrenKnownIssue => CreateGeometryGroupChildrenPath(options, payload, cycle, cardIndex),
			_ => throw new NotSupportedException(options.Target.ToString())
		};
	}

	static TrackedShapeVisual CreatePathFigureSegmentsPath(ReproOptions options, LeakPayloadViewModel payload, int cycle, int cardIndex)
	{
		var figure = new PathFigure
		{
			StartPoint = new Point(7, 17),
			IsClosed = true,
			IsFilled = true
		};

		AddTransientSegments(figure, options, cycle, cardIndex);
		RemoveTransientSegments(figure, options);
		AddVisibleSegments(figure, cardIndex);

		var geometry = new PathGeometry
		{
			FillRule = FillRule.Nonzero
		};
		geometry.Figures.Add(figure);

		return new TrackedShapeVisual(CreatePath(payload, geometry), figure);
	}

	static TrackedShapeVisual CreatePathGeometryFiguresPath(ReproOptions options, LeakPayloadViewModel payload, int cycle, int cardIndex)
	{
		var geometry = new PathGeometry
		{
			FillRule = FillRule.Nonzero
		};

		AddTransientFigures(geometry, options, cycle, cardIndex);
		RemoveTransientFigures(geometry, options);
		AddVisibleFigures(geometry, cardIndex);

		return new TrackedShapeVisual(CreatePath(payload, geometry), geometry);
	}

	static TrackedShapeVisual CreateGeometryGroupChildrenPath(ReproOptions options, LeakPayloadViewModel payload, int cycle, int cardIndex)
	{
		var group = new GeometryGroup
		{
			FillRule = FillRule.Nonzero
		};

		AddTransientGeometries(group, options, cycle, cardIndex);
		RemoveTransientGeometries(group, options);
		AddVisibleGeometries(group, cardIndex);

		return new TrackedShapeVisual(CreatePath(payload, group), group);
	}

	static ShapePath CreatePath(LeakPayloadViewModel payload, Geometry geometry)
	{
		return new ShapePath
		{
			Data = geometry,
			BindingContext = payload,
			WidthRequest = 56,
			HeightRequest = 56,
			Aspect = Stretch.Uniform,
			Fill = Color.FromArgb("#DCEAFE"),
			Stroke = Color.FromArgb("#194A8D"),
			StrokeThickness = 2,
			BackgroundColor = Colors.Transparent
		};
	}

	static void AddTransientSegments(PathFigure figure, ReproOptions options, int cycle, int cardIndex)
	{
		for (var i = 0; i < options.SharedItemsPerCard; i++)
		{
			var fragmentIndex = GetFragmentIndex(cycle, cardIndex, i);
			var segment = options.UsesSharedItems
				? SharedPathSegments[fragmentIndex % SharedPathSegments.Length]
				: CreatePathSegmentFragment(fragmentIndex);

			figure.Segments.Add(segment);
		}
	}

	static void RemoveTransientSegments(PathFigure figure, ReproOptions options)
	{
		if (options.RemoveItemsIndividually)
		{
			while (figure.Segments.Count > 0)
				figure.Segments.RemoveAt(figure.Segments.Count - 1);
		}
		else
		{
			figure.Segments.Clear();
		}
	}

	static void AddVisibleSegments(PathFigure figure, int cardIndex)
	{
		var lane = cardIndex % 4;
		var y = lane * 3;

		figure.StartPoint = new Point(7, 18 + y);
		figure.Segments.Add(new LineSegment(new Point(17, 9)));
		figure.Segments.Add(new LineSegment(new Point(43, 9)));
		figure.Segments.Add(new LineSegment(new Point(50, 18 + y)));
		figure.Segments.Add(new LineSegment(new Point(50, 43)));
		figure.Segments.Add(new LineSegment(new Point(7, 43)));
	}

	static void AddTransientFigures(PathGeometry geometry, ReproOptions options, int cycle, int cardIndex)
	{
		for (var i = 0; i < options.SharedItemsPerCard; i++)
		{
			var fragmentIndex = GetFragmentIndex(cycle, cardIndex, i);
			var figure = options.UsesSharedItems
				? SharedPathFigures[fragmentIndex % SharedPathFigures.Length]
				: CreatePathFigureFragment(fragmentIndex);

			geometry.Figures.Add(figure);
		}
	}

	static void RemoveTransientFigures(PathGeometry geometry, ReproOptions options)
	{
		if (options.RemoveItemsIndividually)
		{
			while (geometry.Figures.Count > 0)
				geometry.Figures.RemoveAt(geometry.Figures.Count - 1);
		}
		else
		{
			geometry.Figures.Clear();
		}
	}

	static void AddVisibleFigures(PathGeometry geometry, int cardIndex)
	{
		geometry.Figures.Add(CreateVisibleCardOutline(cardIndex));
		geometry.Figures.Add(CreateVisibleCardChart(cardIndex));
	}

	static void AddTransientGeometries(GeometryGroup group, ReproOptions options, int cycle, int cardIndex)
	{
		for (var i = 0; i < options.SharedItemsPerCard; i++)
		{
			var fragmentIndex = GetFragmentIndex(cycle, cardIndex, i);
			var fragment = options.UsesSharedItems
				? SharedGeometries[fragmentIndex % SharedGeometries.Length]
				: CreateGeometryFragment(fragmentIndex);

			group.Children.Add(fragment);
		}
	}

	static void RemoveTransientGeometries(GeometryGroup group, ReproOptions options)
	{
		if (options.RemoveItemsIndividually)
		{
			while (group.Children.Count > 0)
				group.Children.RemoveAt(group.Children.Count - 1);
		}
		else
		{
			group.Children.Clear();
		}
	}

	static void AddVisibleGeometries(GeometryGroup group, int cardIndex)
	{
		var lane = cardIndex % 4;
		var y = 9 + (lane * 3);

		group.Children.Add(new RectangleGeometry(new Rect(6, 14, 44, 30)));
		group.Children.Add(new RectangleGeometry(new Rect(13, 7, 30, 12)));
		group.Children.Add(new EllipseGeometry(new Point(19, y + 14), 4, 4));
		group.Children.Add(new EllipseGeometry(new Point(31, y + 14), 4, 4));
		group.Children.Add(new RectangleGeometry(new Rect(15, 34, 26, 4)));
	}

	static int GetFragmentIndex(int cycle, int cardIndex, int itemIndex)
	{
		return Math.Abs((cycle * 31) + (cardIndex * 17) + itemIndex);
	}

	static PathSegment CreatePathSegmentFragment(int seed)
	{
		var offset = seed % 11;

		return (seed % 3) switch
		{
			0 => new LineSegment(new Point(12 + offset, 15 + (offset % 7))),
			1 => new QuadraticBezierSegment(
				new Point(18 + offset, 8 + (offset % 5)),
				new Point(34 + offset, 23 + (offset % 6))),
			_ => new BezierSegment(
				new Point(11 + offset, 12 + (offset % 4)),
				new Point(29 + offset, 8 + (offset % 8)),
				new Point(43 - offset, 31 + (offset % 5)))
		};
	}

	static PathFigure CreatePathFigureFragment(int seed)
	{
		var offset = seed % 9;
		var figure = new PathFigure
		{
			StartPoint = new Point(6 + offset, 14 + (offset % 5)),
			IsClosed = true,
			IsFilled = true
		};

		figure.Segments.Add(new LineSegment(new Point(22 + offset, 8 + (offset % 4))));
		figure.Segments.Add(new LineSegment(new Point(44 - offset, 22 + (offset % 6))));
		figure.Segments.Add(new LineSegment(new Point(15 + offset, 41 - (offset % 5))));

		return figure;
	}

	static Geometry CreateGeometryFragment(int seed)
	{
		var offset = seed % 11;

		return (seed % 3) switch
		{
			0 => new RectangleGeometry(new Rect(3 + offset, 8 + (offset % 4), 38, 18)),
			1 => new EllipseGeometry(new Point(18 + offset, 19 + (offset % 5)), 6 + (offset % 3), 5 + (offset % 4)),
			_ => new LineGeometry(new Point(6 + offset, 12 + (offset % 7)), new Point(46 - offset, 42 - (offset % 5)))
		};
	}

	static PathFigure CreateVisibleCardOutline(int cardIndex)
	{
		var lane = cardIndex % 4;
		var y = lane * 3;
		var figure = new PathFigure
		{
			StartPoint = new Point(7, 18 + y),
			IsClosed = true,
			IsFilled = true
		};

		figure.Segments.Add(new LineSegment(new Point(17, 9)));
		figure.Segments.Add(new LineSegment(new Point(43, 9)));
		figure.Segments.Add(new LineSegment(new Point(50, 18 + y)));
		figure.Segments.Add(new LineSegment(new Point(50, 43)));
		figure.Segments.Add(new LineSegment(new Point(7, 43)));

		return figure;
	}

	static PathFigure CreateVisibleCardChart(int cardIndex)
	{
		var lane = cardIndex % 4;
		var figure = new PathFigure
		{
			StartPoint = new Point(15, 34 - lane),
			IsClosed = false,
			IsFilled = false
		};

		figure.Segments.Add(new LineSegment(new Point(23, 26 - lane)));
		figure.Segments.Add(new LineSegment(new Point(31, 31 - lane)));
		figure.Segments.Add(new LineSegment(new Point(41, 20 + lane)));

		return figure;
	}
}
