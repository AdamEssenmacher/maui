namespace GradientBrushGradientStopsLeakRepro;

public sealed class LeakPage : ContentPage
{
	readonly IReadOnlyList<GradientBrush> _brushes;

	public LeakPage()
	{
		var session = ReproSession.Current ?? throw new InvalidOperationException("No active repro session.");
		var options = session.Options;
		var cycle = session.CurrentCycle;
		var payload = new LeakPayloadViewModel(cycle, options.PayloadBytesPerPage, options.BrushesPerPage);

		Title = payload.Title;
		BindingContext = payload;

		var brushes = new List<GradientBrush>(options.BrushesPerPage);
		var containers = new List<Border>(options.BrushesPerPage);
		var cards = new VerticalStackLayout
		{
			Spacing = 10
		};

		for (var i = 0; i < options.BrushesPerPage; i++)
		{
			var brush = new LinearGradientBrush(session.CreateGradientStops(), new Point(0, 0), new Point(1, 1))
			{
				BindingContext = payload,
				StartPoint = new Point((cycle + i) % 3 / 4d, 0),
				EndPoint = new Point(1, 1)
			};

			var card = new Border
			{
				BindingContext = payload,
				Background = brush,
				Padding = new Thickness(16),
				StrokeThickness = 0,
				HeightRequest = 72,
				Content = new Label
				{
					Text = $"Gradient surface {i + 1}",
					TextColor = Colors.White,
					FontSize = 15,
					FontAttributes = FontAttributes.Bold,
					VerticalTextAlignment = TextAlignment.Center
				}
			};

			brushes.Add(brush);
			containers.Add(card);
			cards.Children.Add(card);
		}

		_brushes = brushes;
		session.Track(this, containers, _brushes, payload);

		Content = new ScrollView
		{
			Content = new VerticalStackLayout
			{
				Padding = new Thickness(18),
				Spacing = 12,
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
						Text = $"{options.Name}: {options.BrushesPerPage} brushes, {options.StopsPerBrush} gradient stops, {options.PayloadMegabytesPerPage} MB cached payload",
						FontSize = 14,
						TextColor = Color.FromArgb("#57606A")
					},
					cards
				}
			}
		};
	}

	protected override void OnDisappearing()
	{
		if (ReproSession.Current?.Options.ClearGradientStopsOnDisappear == true)
		{
			foreach (var brush in _brushes)
				brush.GradientStops = new GradientStopCollection();
		}

		base.OnDisappearing();
	}
}
