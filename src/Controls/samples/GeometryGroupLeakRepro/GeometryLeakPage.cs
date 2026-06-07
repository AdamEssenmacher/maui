using ShapePath = Microsoft.Maui.Controls.Shapes.Path;

namespace GeometryGroupLeakRepro;

public sealed class GeometryLeakPage : ContentPage
{
	public GeometryLeakPage()
	{
		var session = ReproSession.Current ?? throw new InvalidOperationException("No active repro session.");
		var options = session.Options;
		var cycle = session.CurrentCycle;
		var payload = new LeakPayloadViewModel(cycle, options.PayloadBytesPerPage);
		var trackedVisuals = new List<TrackedGeometryVisual>(options.CardsPerPage);

		Title = payload.Title;
		BindingContext = payload;
		BackgroundColor = Color.FromArgb("#F6F8FA");

		var cards = new VerticalStackLayout
		{
			Spacing = 10
		};

		for (var i = 0; i < options.CardsPerPage; i++)
		{
			var visual = GeometryCardFactory.CreateTrackedPath(options, payload, cycle, i);
			trackedVisuals.Add(visual);
			cards.Children.Add(CreateCaseCard(payload, options, i, visual.Path));
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
						Text = $"{options.Name}: {options.CardsPerPage} cards, {options.SharedFragmentsPerCard} transient geometry fragments per card, {options.PayloadMegabytesPerPage} MB cached case payload.",
						FontSize = 13,
						TextColor = Color.FromArgb("#57606A")
					},
					cards
				}
			}
		};
	}

	static Border CreateCaseCard(LeakPayloadViewModel payload, ReproOptions options, int cardIndex, ShapePath iconPath)
	{
		var item = payload.RecentCases[cardIndex % payload.RecentCases.Count];
		var badgeColor = item.Status == "Needs review" ? Color.FromArgb("#9A3412") : Color.FromArgb("#0F6B5B");

		var grid = new Grid
		{
			ColumnDefinitions =
			{
				new ColumnDefinition(new GridLength(70)),
				new ColumnDefinition(GridLength.Star),
				new ColumnDefinition(GridLength.Auto)
			},
			ColumnSpacing = 12,
			VerticalOptions = LayoutOptions.Center
		};

		var iconHost = new Grid
		{
			WidthRequest = 64,
			HeightRequest = 64,
			Padding = 4,
			BackgroundColor = Color.FromArgb("#EEF4FF")
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
					Text = $"{options.Name}, card {cardIndex + 1}",
					FontSize = 11,
					TextColor = Color.FromArgb("#6E7781"),
					LineBreakMode = LineBreakMode.TailTruncation
				}
			}
		};

		var badge = new Label
		{
			Text = item.Status,
			FontSize = 11,
			TextColor = badgeColor,
			VerticalOptions = LayoutOptions.Center,
			HorizontalTextAlignment = TextAlignment.End
		};

		grid.Add(iconHost, 0, 0);
		grid.Add(text, 1, 0);
		grid.Add(badge, 2, 0);

		return new Border
		{
			Stroke = Color.FromArgb("#D0D7DE"),
			StrokeThickness = 1,
			BackgroundColor = Colors.White,
			Padding = new Thickness(12),
			MinimumHeightRequest = 88,
			Content = grid
		};
	}
}
