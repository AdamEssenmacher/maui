using Microsoft.Maui.Controls.Shapes;

namespace BorderDashArrayLeakRepro;

public sealed class BorderLeakPage : ContentPage
{
	readonly CollectionView _collectionView;
	readonly PagePayloadViewModel _payload;
	int _tapCount;

	public BorderLeakPage()
	{
		var session = ReproSession.Current ?? throw new InvalidOperationException("No active repro session.");
		var options = session.Options;
		var cycle = session.CurrentCycle;

		_payload = new PagePayloadViewModel(cycle, options);
		Title = _payload.Title;
		BindingContext = _payload;
		BackgroundColor = Color.FromArgb("#F8FAFC");

		_collectionView = new CollectionView
		{
			ItemsSource = _payload.Cards,
			ItemsLayout = new GridItemsLayout(2, ItemsLayoutOrientation.Vertical)
			{
				HorizontalItemSpacing = 12,
				VerticalItemSpacing = 12
			},
			ItemSizingStrategy = ItemSizingStrategy.MeasureFirstItem,
			ItemTemplate = new DataTemplate(() => CreateCardView(options, session)),
			Header = CreateHeader(options),
			Footer = new BoxView { HeightRequest = 24, Opacity = 0 },
			Margin = new Thickness(14, 0)
		};

		session.TrackPage(this, _collectionView, _payload);

		Content = _collectionView;
	}

	View CreateHeader(ReproOptions options)
	{
		return new VerticalStackLayout
		{
			Padding = new Thickness(0, 18, 0, 14),
			Spacing = 8,
			Children =
			{
				new Label
				{
					Text = _payload.Title,
					FontSize = 22,
					FontAttributes = FontAttributes.Bold,
					TextColor = Color.FromArgb("#0F172A")
				},
				new Label
				{
					Text = $"{options.Name}: {_payload.Cards.Count} account cards, {ReproStats.FormatBytes(options.PayloadBytesPerPage)} simulated page/item payload.",
					FontSize = 13,
					TextColor = Color.FromArgb("#475569")
				}
			}
		};
	}

	View CreateCardView(ReproOptions options, ReproSession session)
	{
		var title = new Label
		{
			FontSize = 15,
			FontAttributes = FontAttributes.Bold,
			TextColor = Color.FromArgb("#0F172A"),
			LineBreakMode = LineBreakMode.TailTruncation
		};
		title.SetBinding(Label.TextProperty, nameof(CardPayloadViewModel.Title));

		var status = new Label
		{
			FontSize = 12,
			TextColor = Color.FromArgb("#2563EB"),
			LineBreakMode = LineBreakMode.TailTruncation
		};
		status.SetBinding(Label.TextProperty, nameof(CardPayloadViewModel.Status));

		var owner = new Label
		{
			FontSize = 12,
			TextColor = Color.FromArgb("#64748B"),
			LineBreakMode = LineBreakMode.TailTruncation
		};
		owner.SetBinding(Label.TextProperty, nameof(CardPayloadViewModel.Owner), stringFormat: "Owner: {0}");

		var amount = new Label
		{
			FontSize = 18,
			FontAttributes = FontAttributes.Bold,
			TextColor = Color.FromArgb("#14532D"),
			HorizontalTextAlignment = TextAlignment.End
		};
		amount.SetBinding(Label.TextProperty, nameof(CardPayloadViewModel.AmountText));

		var body = new Grid
		{
			ColumnDefinitions =
			{
				new ColumnDefinition(GridLength.Star),
				new ColumnDefinition(GridLength.Auto)
			},
			RowDefinitions =
			{
				new RowDefinition(GridLength.Auto),
				new RowDefinition(GridLength.Auto),
				new RowDefinition(GridLength.Auto)
			},
			ColumnSpacing = 10,
			RowSpacing = 4
		};

		body.Add(title, 0, 0);
		body.Add(status, 0, 1);
		body.Add(owner, 0, 2);
		body.Add(amount, 1, 0);
		Grid.SetRowSpan(amount, 3);

		var border = new Border
		{
			Stroke = Color.FromArgb("#64748B"),
			StrokeThickness = 1.5,
			StrokeShape = new RoundRectangle { CornerRadius = 6 },
			Padding = new Thickness(12),
			BackgroundColor = Colors.White,
			HeightRequest = 106,
			MinimumHeightRequest = 106,
			Content = body
		};

		if (options.UseSharedDashArray)
		{
			border.StrokeDashArray = GetSharedDashArray();
		}
		else if (options.UsePerBorderDashArray)
		{
			border.StrokeDashArray = CreateDashArray();
		}

		var tapGesture = new TapGestureRecognizer();
		tapGesture.Tapped += OnCardTapped;
		border.GestureRecognizers.Add(tapGesture);

		var tracked = false;
		border.BindingContextChanged += (_, _) =>
		{
			if (!tracked && border.BindingContext is CardPayloadViewModel card)
			{
				tracked = true;
				session.TrackCardBorder(border, card);
			}
		};

		return border;
	}

	void OnCardTapped(object? sender, TappedEventArgs e)
	{
		_tapCount++;

		if (sender is BindableObject bindable && bindable.BindingContext is CardPayloadViewModel card)
			_payload.OpenCardCommand.Execute(card);
	}

	static DoubleCollection GetSharedDashArray()
	{
		if (Application.Current?.Resources.TryGetValue(App.SharedDashArrayResourceKey, out var value) == true &&
			value is DoubleCollection dashArray)
		{
			return dashArray;
		}

		throw new InvalidOperationException("The shared dash array resource is missing.");
	}

	static DoubleCollection CreateDashArray() => new(new[] { 6d, 3d, 1d, 3d });
}
