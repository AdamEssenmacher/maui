using Microsoft.Maui.Controls.Shapes;

namespace IndicatorViewItemsSourceLeakRepro;

public sealed class IndicatorFeedPage : ContentPage
{
	readonly IndicatorView _indicatorView;
	readonly CarouselView _carouselView;

	public IndicatorFeedPage()
	{
		var session = ReproSession.Current ?? throw new InvalidOperationException("No active repro session.");
		var options = session.Options;
		var cycle = session.CurrentCycle;
		var viewModel = new VisitPayloadViewModel(cycle);
		var indicatorPayload = new RetainedPayloadBehavior("IndicatorView behavior", cycle, options.IndicatorPayloadBytesPerVisit);
		var carouselPayload = new RetainedPayloadBehavior("CarouselView behavior", cycle, options.CarouselPayloadBytesPerVisit);
		var itemsSource = options.UseObservableFeed ? session.Feed.LiveCards : session.Feed.CreateSnapshot();

		Title = viewModel.Title;
		BindingContext = viewModel;

		_carouselView = new CarouselView
		{
			ItemsSource = itemsSource,
			HeightRequest = 280,
			PeekAreaInsets = new Thickness(18, 0),
			ItemTemplate = new DataTemplate(CreateCardView)
		};
		_carouselView.Behaviors.Add(carouselPayload);

		_indicatorView = new IndicatorView
		{
			MaximumVisible = 9,
			IndicatorSize = 8,
			IndicatorColor = Color.FromArgb("#B7C7BD"),
			SelectedIndicatorColor = Color.FromArgb("#146C5A"),
			HorizontalOptions = LayoutOptions.Center,
			Margin = new Thickness(0, 8, 0, 0)
		};
		_indicatorView.Behaviors.Add(indicatorPayload);

		_carouselView.IndicatorView = _indicatorView;

		var header = new VerticalStackLayout
		{
			Spacing = 4,
			Children =
			{
				new Label
				{
					Text = viewModel.Title,
					FontSize = 20,
					FontAttributes = FontAttributes.Bold,
					TextColor = Color.FromArgb("#0B1F33")
				},
				new Label
				{
					Text = $"{options.Name}: {options.FeedItems} shared feed cards, {options.ControlPayloadMegabytesPerVisit} MB attached control payload",
					FontSize = 13,
					TextColor = Color.FromArgb("#59665F")
				}
			}
		};

		var detail = new Label
		{
			Text = "The IndicatorView is linked through CarouselView.IndicatorView, which binds IndicatorView.ItemsSource to the carousel source.",
			FontSize = 13,
			TextColor = Color.FromArgb("#59665F"),
			LineBreakMode = LineBreakMode.WordWrap
		};

		var payloadPanel = CreatePayloadPanel(viewModel, indicatorPayload, carouselPayload);
		var rootLayout = new VerticalStackLayout
		{
			Padding = new Thickness(18),
			Spacing = 14,
			Children =
			{
				header,
				_carouselView,
				_indicatorView,
				payloadPanel,
				detail
			}
		};
		var scrollView = new ScrollView { Content = rootLayout };
		Content = scrollView;

		scrollView.BindingContext = null;
		rootLayout.BindingContext = null;
		header.BindingContext = null;
		_carouselView.BindingContext = null;
		_indicatorView.BindingContext = null;
		payloadPanel.BindingContext = null;
		detail.BindingContext = null;

		var trackedCycle = session.Track(this, _indicatorView, _carouselView, viewModel, indicatorPayload, carouselPayload);
		_indicatorView.HandlerChanging += (_, args) =>
		{
			trackedCycle.CaptureIndicatorHandler(args.OldHandler);
		};
		_indicatorView.HandlerChanged += (sender, _) =>
		{
			if (sender is IndicatorView indicatorView)
				trackedCycle.CaptureIndicatorHandler(indicatorView.Handler);
		};
		_carouselView.HandlerChanging += (_, args) =>
		{
			trackedCycle.CaptureCarouselHandler(args.OldHandler);
		};
		_carouselView.HandlerChanged += (sender, _) =>
		{
			if (sender is CarouselView carouselView)
				trackedCycle.CaptureCarouselHandler(carouselView.Handler);
		};
		trackedCycle.CaptureIndicatorHandler(_indicatorView.Handler);
		trackedCycle.CaptureCarouselHandler(_carouselView.Handler);
	}

	protected override void OnDisappearing()
	{
		if (ReproSession.Current?.Options.ClearIndicatorOnDisappear == true)
		{
			_indicatorView.RemoveBinding(IndicatorView.PositionProperty);
			_indicatorView.RemoveBinding(IndicatorView.ItemsSourceProperty);
			_indicatorView.ItemsSource = null;
			_carouselView.ItemsSource = null;
		}

		base.OnDisappearing();
	}

	static View CreateCardView()
	{
		var title = new Label
		{
			FontSize = 18,
			FontAttributes = FontAttributes.Bold,
			TextColor = Colors.White
		};
		title.SetBinding(Label.TextProperty, nameof(DashboardCard.Title));

		var subtitle = new Label
		{
			FontSize = 13,
			TextColor = Color.FromArgb("#DDEBE4")
		};
		subtitle.SetBinding(Label.TextProperty, nameof(DashboardCard.Subtitle));

		var amount = new Label
		{
			FontSize = 30,
			FontAttributes = FontAttributes.Bold,
			TextColor = Colors.White
		};
		amount.SetBinding(Label.TextProperty, nameof(DashboardCard.AmountText));

		var status = new Label
		{
			FontSize = 12,
			TextColor = Colors.White,
			BackgroundColor = Color.FromArgb("#2C8C73"),
			Padding = new Thickness(8, 3),
			HorizontalOptions = LayoutOptions.Start
		};
		status.SetBinding(Label.TextProperty, nameof(DashboardCard.Status));

		return new Border
		{
			StrokeThickness = 0,
			BackgroundColor = Color.FromArgb("#146C5A"),
			StrokeShape = new RoundRectangle { CornerRadius = 8 },
			Margin = new Thickness(6, 0),
			Padding = new Thickness(18),
			Content = new VerticalStackLayout
			{
				Spacing = 10,
				Children =
				{
					title,
					subtitle,
					amount,
					status
				}
			}
		};
	}

	static View CreatePayloadPanel(
		VisitPayloadViewModel viewModel,
		RetainedPayloadBehavior indicatorPayload,
		RetainedPayloadBehavior carouselPayload)
	{
		return new Border
		{
			StrokeThickness = 1,
			Stroke = Color.FromArgb("#D6E1DB"),
			BackgroundColor = Color.FromArgb("#F3F7F4"),
			StrokeShape = new RoundRectangle { CornerRadius = 8 },
			Padding = new Thickness(12),
			Content = new VerticalStackLayout
			{
				Spacing = 4,
				Children =
				{
					new Label
					{
						Text = "Control-attached payload",
						FontAttributes = FontAttributes.Bold,
						FontSize = 13,
						TextColor = Color.FromArgb("#25312D")
					},
					new Label
					{
						Text = string.Join(Environment.NewLine,
							viewModel.Description,
							indicatorPayload.Description,
							carouselPayload.Description),
						FontSize = 13,
						TextColor = Color.FromArgb("#59665F")
					}
				}
			}
		};
	}
}
