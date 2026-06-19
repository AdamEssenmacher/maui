using Microsoft.Maui.Controls.Shapes;
using ShapePath = Microsoft.Maui.Controls.Shapes.Path;

namespace TransformGroupLeakRepro;

public sealed class TransformLeakPage : ContentPage
{
	static readonly PathGeometryConverter PathConverter = new();

	public TransformLeakPage()
	{
		var session = ReproSession.Current ?? throw new InvalidOperationException("No active repro session.");
		var options = session.Options;
		var cycle = session.CurrentCycle;
		var payload = new LeakPayloadViewModel(cycle, options.PayloadBytesPerPage, options.PathsPerPage);
		var paths = new List<ShapePath>(options.PathsPerPage);
		var groups = new List<TransformGroup>(options.PathsPerPage);

		Title = payload.Title;
		BindingContext = payload;

		var chartGrid = CreateChartGrid(options.PathsPerPage);

		for (var i = 0; i < options.PathsPerPage; i++)
		{
			var path = CreateMetricPath(i);
			path.BindingContext = payload;

			var group = new TransformGroup();
			path.RenderTransform = group;

			var childTransform = session.CreateChildTransform(i);
			group.Children.Add(childTransform);

			if (options.RemoveSharedTransformBeforeReplace)
				group.Children.Remove(childTransform);

			group.Children = new TransformCollection();

			paths.Add(path);
			groups.Add(group);

			chartGrid.Add(CreateMetricTile(payload.Metrics[i], path, options, cycle), i % 3, i / 3);
		}

		session.Track(this, paths, groups, payload);

		Content = new ScrollView
		{
			Content = new VerticalStackLayout
			{
				Padding = new Thickness(16, 16, 16, 24),
				Spacing = 14,
				Children =
				{
					new Label
					{
						Text = payload.Title,
						FontSize = 22,
						FontAttributes = FontAttributes.Bold,
						TextColor = Color.FromArgb("#0B1F33")
					},
					new Label
					{
						Text = $"{options.Name}: {options.PathsPerPage} transformed vector Paths, {options.PayloadMegabytesPerPage} MB cached payload",
						FontSize = 13,
						TextColor = Color.FromArgb("#57606A")
					},
					chartGrid
				}
			}
		};
	}

	static Grid CreateChartGrid(int pathsPerPage)
	{
		var grid = new Grid
		{
			ColumnSpacing = 10,
			RowSpacing = 10
		};

		for (var column = 0; column < 3; column++)
			grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));

		for (var row = 0; row < Math.Ceiling(pathsPerPage / 3d); row++)
			grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

		return grid;
	}

	static Border CreateMetricTile(DashboardMetric metric, ShapePath path, ReproOptions options, int cycle)
	{
		return new Border
		{
			Stroke = Color.FromArgb("#D0D7DE"),
			StrokeShape = new RoundRectangle { CornerRadius = 6 },
			BackgroundColor = Color.FromArgb("#F6F8FA"),
			Padding = new Thickness(10),
			Content = new VerticalStackLayout
			{
				Spacing = 6,
				Children =
				{
					new Label
					{
						Text = metric.Id,
						FontSize = 11,
						FontAttributes = FontAttributes.Bold,
						TextColor = Color.FromArgb("#57606A")
					},
					new Label
					{
						Text = metric.Summary,
						FontSize = 12,
						TextColor = Color.FromArgb("#172026"),
						LineBreakMode = LineBreakMode.TailTruncation
					},
					new Grid
					{
						HeightRequest = 64,
						Children =
						{
							path,
							new Label
							{
								Text = $"{metric.Value + cycle % 9}%",
								HorizontalOptions = LayoutOptions.End,
								VerticalOptions = LayoutOptions.Start,
								FontSize = 12,
								TextColor = options.Mode == ReproMode.SharedTransform ? Color.FromArgb("#9A3412") : Color.FromArgb("#0F766E")
							}
						}
					}
				}
			}
		};
	}

	static ShapePath CreateMetricPath(int index)
	{
		var startY = 44 - index % 4 * 4;
		var peakY = 12 + index % 5 * 3;
		var endY = 22 + index % 6 * 3;
		var geometry = (Geometry?)PathConverter.ConvertFromInvariantString(
			$"M 0,{startY} C 18,{peakY} 34,{52 - peakY} 50,{28 + index % 8} S 84,{endY} 104,{18 + index % 9}")
			?? throw new InvalidOperationException("Failed to create metric path geometry.");

		return new ShapePath
		{
			Data = geometry,
			Aspect = Stretch.Fill,
			Stroke = index % 2 == 0 ? Color.FromArgb("#0F766E") : Color.FromArgb("#2563EB"),
			StrokeThickness = 3,
			StrokeLineCap = PenLineCap.Round,
			Fill = Brush.Transparent,
			HeightRequest = 58,
			HorizontalOptions = LayoutOptions.Fill,
			VerticalOptions = LayoutOptions.Fill
		};
	}
}
