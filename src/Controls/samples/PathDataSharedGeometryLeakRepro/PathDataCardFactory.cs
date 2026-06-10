using Microsoft.Maui.Controls.Shapes;
using Microsoft.Maui.Graphics;
using ShapePath = Microsoft.Maui.Controls.Shapes.Path;

namespace PathDataSharedGeometryLeakRepro;

internal static class PathDataCardFactory
{
	public static TrackedPathVisual CreateTrackedPath(ReproOptions options, LeakPayloadViewModel payload, int cycle, int pathIndex)
	{
		var path = new ShapePath
		{
			Data = options.UsesSharedSources ? GetSharedResource<PathGeometry>(App.SharedPathGeometryResourceKey) : CreateFreshGeometry(cycle, pathIndex),
			RenderTransform = options.UsesSharedSources ? GetSharedResource<ScaleTransform>(App.SharedScaleTransformResourceKey) : new ScaleTransform(1, 1, 12, 12),
			BindingContext = payload,
			WidthRequest = 44,
			HeightRequest = 44,
			Aspect = Stretch.Uniform,
			Fill = Color.FromArgb("#E6F5F8"),
			Stroke = Color.FromArgb("#144C5A"),
			StrokeThickness = 2,
			BackgroundColor = Colors.Transparent
		};

		return new TrackedPathVisual(path);
	}

	public static PathGeometry CreateSharedGeometry()
	{
		return CreateStatusGeometry(0);
	}

	static PathGeometry CreateFreshGeometry(int cycle, int pathIndex)
	{
		return CreateStatusGeometry((cycle * 37) + pathIndex);
	}

	static PathGeometry CreateStatusGeometry(int seed)
	{
		var lane = seed % 4;
		var notch = lane + 1;

		return new PathGeometry(
			new PathFigureCollection
			{
				new PathFigure
				{
					StartPoint = new Point(12, 2),
					IsClosed = true,
					Segments =
					{
						new LineSegment(new Point(22, 20)),
						new LineSegment(new Point(2, 20))
					}
				},
				new PathFigure
				{
					StartPoint = new Point(12, 7 + notch),
					Segments =
					{
						new LineSegment(new Point(12, 13 + notch))
					}
				},
				new PathFigure
				{
					StartPoint = new Point(12, 17),
					Segments =
					{
						new LineSegment(new Point(12.1, 17))
					}
				}
			},
			FillRule.Nonzero);
	}

	static T GetSharedResource<T>(string key) where T : class
	{
		if (Application.Current?.Resources.TryGetValue(key, out var value) == true && value is T typed)
			return typed;

		throw new InvalidOperationException($"Missing shared resource '{key}'.");
	}
}
