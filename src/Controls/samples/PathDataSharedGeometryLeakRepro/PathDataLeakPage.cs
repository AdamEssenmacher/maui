using ShapePath = Microsoft.Maui.Controls.Shapes.Path;

namespace PathDataSharedGeometryLeakRepro;

public sealed class PathDataLeakPage : ContentPage
{
	readonly ReproOptions _options;
	readonly List<ShapePath> _paths = new();
	bool _clearedPathSources;

	public PathDataLeakPage()
	{
		var session = ReproSession.Current ?? throw new InvalidOperationException("No active repro session.");
		_options = session.Options;
		var cycle = session.CurrentCycle;
		var payload = new LeakPayloadViewModel(cycle, _options.PayloadBytesPerPage);
		var trackedVisuals = new List<TrackedPathVisual>(_options.PathsPerPage);

		Title = payload.Title;
		BindingContext = payload;
		BackgroundColor = Color.FromArgb("#F6F8FA");

		var rows = new VerticalStackLayout
		{
			Spacing = 10
		};

		for (var i = 0; i < _options.PathsPerPage; i++)
		{
			var visual = PathDataCardFactory.CreateTrackedPath(_options, payload, cycle, i);
			trackedVisuals.Add(visual);
			_paths.Add(visual.Path);
			rows.Children.Add(CreateDashboardRow(payload, _options, i, visual.Path));
		}

		session.Track(this, payload, trackedVisuals);

		Content = new ScrollView
		{
			Content = new VerticalStackLayout
			{
				Padding = new Thickness(16, 14, 16, 24),
				Spacing = 14,
				Children =
				{
					new Label
					{
						Text = payload.Title,
						FontSize = 22,
						FontAttributes = FontAttributes.Bold,
						TextColor = Color.FromArgb("#172026")
					},
					new Label
					{
						Text = $"{_options.Name}: {_options.PathsPerPage} path indicators and {_options.PayloadMegabytesPerPage} MB cached dashboard payload.",
						FontSize = 13,
						TextColor = Color.FromArgb("#57606A")
					},
					rows
				}
			}
		};
	}

	protected override void OnDisappearing()
	{
		if (_options.ClearPathSourcesOnDisappear && !_clearedPathSources)
		{
			foreach (var path in _paths)
			{
				path.Data = null;
				path.RenderTransform = null;
			}

			_clearedPathSources = true;
		}

		base.OnDisappearing();
	}

	static Border CreateDashboardRow(LeakPayloadViewModel payload, ReproOptions options, int pathIndex, ShapePath iconPath)
	{
		var item = payload.Panels[pathIndex % payload.Panels.Count];
		var statusColor = item.Status == "Escalated" ? Color.FromArgb("#9A3412") : Color.FromArgb("#0F6B5B");

		var grid = new Grid
		{
			ColumnDefinitions =
			{
				new ColumnDefinition(new GridLength(58)),
				new ColumnDefinition(GridLength.Star),
				new ColumnDefinition(GridLength.Auto)
			},
			ColumnSpacing = 12,
			VerticalOptions = LayoutOptions.Center
		};

		var iconHost = new Grid
		{
			WidthRequest = 52,
			HeightRequest = 52,
			Padding = 4,
			BackgroundColor = Color.FromArgb("#E6F5F8")
		};
		iconHost.Children.Add(iconPath);

		var text = new VerticalStackLayout
		{
			Spacing = 3,
			VerticalOptions = LayoutOptions.Center,
			Children =
			{
				new Label
				{
					Text = item.Id,
					FontSize = 13,
					FontAttributes = FontAttributes.Bold,
					TextColor = Color.FromArgb("#172026")
				},
				new Label
				{
					Text = item.Summary,
					FontSize = 13,
					TextColor = Color.FromArgb("#57606A"),
					LineBreakMode = LineBreakMode.TailTruncation
				},
				new Label
				{
					Text = $"{options.Name}, path {pathIndex + 1}",
					FontSize = 11,
					TextColor = Color.FromArgb("#6E7781"),
					LineBreakMode = LineBreakMode.TailTruncation
				}
			}
		};

		var status = new Label
		{
			Text = item.Status,
			FontSize = 11,
			TextColor = statusColor,
			VerticalOptions = LayoutOptions.Center,
			HorizontalTextAlignment = TextAlignment.End
		};

		grid.Add(iconHost, 0, 0);
		grid.Add(text, 1, 0);
		grid.Add(status, 2, 0);

		return new Border
		{
			Stroke = Color.FromArgb("#D0D7DE"),
			StrokeThickness = 1,
			BackgroundColor = Colors.White,
			Padding = new Thickness(12),
			MinimumHeightRequest = 78,
			Content = grid
		};
	}
}
