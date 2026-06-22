using System.Diagnostics;
using Microsoft.Maui.Controls.Shapes;

namespace IndicatorViewTemplateSwapLeakRepro;

internal sealed class IndicatorTemplateHost : ContentView
{
	static readonly BindableProperty LayoutPayloadAnchorProperty = BindableProperty.CreateAttached(
		"LayoutPayloadAnchor",
		typeof(IReadOnlyList<RetainedPayloadBehavior>),
		typeof(IndicatorTemplateHost),
		null);

	readonly ReproSession _session;
	readonly ReproOptions _options;
	readonly CarouselView _carouselView;
	readonly IndicatorView _indicatorView;
	readonly Label _templateLabel;
	readonly DataTemplate _avatarChipTemplate;
	readonly DataTemplate _previewPillTemplate;
	LiveLayoutGeneration? _currentGeneration;

	public IndicatorTemplateHost(ReproSession session)
	{
		_session = session;
		_options = session.Options;
		_avatarChipTemplate = CreateAvatarChipTemplate();
		_previewPillTemplate = CreatePreviewPillTemplate();

		_templateLabel = new Label
		{
			Text = "Preparing initial template...",
			FontSize = 13,
			TextColor = Color.FromArgb("#57606A")
		};

		_carouselView = new CarouselView
		{
			ItemsSource = session.Stories,
			HeightRequest = 260,
			PeekAreaInsets = new Thickness(18, 0),
			Loop = false,
			ItemTemplate = new DataTemplate(CreateStoryCardView)
		};

		_indicatorView = new IndicatorView
		{
			MaximumVisible = session.Stories.Count,
			IndicatorSize = 10,
			IndicatorColor = Color.FromArgb("#B7C7BD"),
			SelectedIndicatorColor = Color.FromArgb("#146C5A"),
			HorizontalOptions = LayoutOptions.Center,
			Margin = new Thickness(0, 8, 0, 0)
		};
		_carouselView.IndicatorView = _indicatorView;

		var explanation = new Label
		{
			Text = $"Each retired indicator child carries {FormatBytes(_options.PayloadBytesPerIndicator)} of synthetic thumbnail cache payload. The live layout is not counted; only retired generations are counted after replacement.",
			FontSize = 13,
			TextColor = Color.FromArgb("#57606A"),
			LineBreakMode = LineBreakMode.WordWrap
		};

		Content = new VerticalStackLayout
		{
			Spacing = 14,
			Children =
			{
				new Label
				{
					Text = "Story carousel host",
					FontSize = 18,
					FontAttributes = FontAttributes.Bold,
					TextColor = Color.FromArgb("#0B1F33")
				},
				_templateLabel,
				_carouselView,
				_indicatorView,
				CreatePayloadPanel(explanation)
			}
		};
	}

	public async Task InitializeAsync(CancellationToken token)
	{
		await ApplyTemplateInternalAsync(0, clearFirst: false, token);
	}

	public async Task ApplyNextTemplateAsync(int generationIndex, bool clearFirst, CancellationToken token)
	{
		await ApplyTemplateInternalAsync(generationIndex, clearFirst, token);
	}

	public async Task<TimeSpan> MeasurePositionUpdateBurstAsync(int updateCount, CancellationToken token)
	{
		if (updateCount <= 0)
			return TimeSpan.Zero;

		await WaitForMaterializedLayoutAsync(token);

		var itemCount = Math.Max(1, _session.Stories.Count);
		var stopwatch = Stopwatch.StartNew();
		for (var index = 0; index < updateCount; index++)
		{
			token.ThrowIfCancellationRequested();
			_indicatorView.Position = (index + 1) % itemCount;
		}

		stopwatch.Stop();
		await Task.Delay(1, token);
		return stopwatch.Elapsed;
	}

	async Task ApplyTemplateInternalAsync(int generationIndex, bool clearFirst, CancellationToken token)
	{
		var previousGeneration = _currentGeneration;

		if (clearFirst && _indicatorView.IndicatorTemplate is not null)
		{
			_indicatorView.IndicatorTemplate = null;
			await WaitForLayoutClearedAsync(token);
		}

		var nextTemplate = SelectTemplate(generationIndex);
		_indicatorView.IndicatorTemplate = nextTemplate.Template;

		var layout = await WaitForMaterializedLayoutAsync(token);
		_session.RecordMaterializedTemplateState(generationIndex);
		var behaviors = AttachPayloadBehaviors(layout, nextTemplate.Name, generationIndex);
		_currentGeneration = new LiveLayoutGeneration(generationIndex, nextTemplate.Name, layout, behaviors);
		_templateLabel.Text = $"Current template: {nextTemplate.Name} ({generationIndex + 1}/{_options.TemplateStateCount})";

		if (previousGeneration is not null)
		{
			_session.TrackRetiredGeneration(
				previousGeneration.GenerationIndex,
				previousGeneration.TemplateName,
				previousGeneration.Layout,
				previousGeneration.PayloadBehaviors);
		}
	}

	( string Name, DataTemplate Template ) SelectTemplate(int generationIndex)
	{
		return generationIndex % 2 == 0
			? ("Avatar chip", _avatarChipTemplate)
			: ("Preview pill", _previewPillTemplate);
	}

	async Task WaitForLayoutClearedAsync(CancellationToken token)
	{
		for (var attempt = 0; attempt < 80; attempt++)
		{
			token.ThrowIfCancellationRequested();

			if (_indicatorView.IndicatorLayout is null)
				return;

			await Task.Delay(16, token);
		}

		throw new TimeoutException("IndicatorView.IndicatorLayout did not clear after setting IndicatorTemplate to null.");
	}

	async Task<Layout> WaitForMaterializedLayoutAsync(CancellationToken token)
	{
		for (var attempt = 0; attempt < 80; attempt++)
		{
			token.ThrowIfCancellationRequested();

			if (_indicatorView.IndicatorLayout is Layout layout && layout.Children.Count == _session.Stories.Count)
				return layout;

			await Task.Delay(16, token);
		}

		throw new TimeoutException($"Indicator template did not materialize {_session.Stories.Count} children.");
	}

	IReadOnlyList<RetainedPayloadBehavior> AttachPayloadBehaviors(Layout layout, string templateName, int generationIndex)
	{
		var attached = new List<RetainedPayloadBehavior>(layout.Children.Count);

		for (var index = 0; index < layout.Children.Count; index++)
		{
			if (layout.Children[index] is not VisualElement child)
				continue;

			var behavior = new RetainedPayloadBehavior(templateName, generationIndex, index, _options.PayloadBytesPerIndicator);
			RetainedPayloadBehavior.AttachTo(child, behavior);
			attached.Add(behavior);
		}

		layout.SetValue(LayoutPayloadAnchorProperty, attached);
		return attached;
	}

	DataTemplate CreateAvatarChipTemplate()
	{
		return new DataTemplate(() =>
		{
			var initials = new Label
			{
				FontSize = 12,
				FontAttributes = FontAttributes.Bold,
				TextColor = Colors.White,
				HorizontalTextAlignment = TextAlignment.Center,
				VerticalTextAlignment = TextAlignment.Center
			};
			initials.SetBinding(Label.TextProperty, nameof(MediaStoryCard.CreatorInitials));

			var avatar = new Border
			{
				WidthRequest = 28,
				HeightRequest = 28,
				StrokeThickness = 0,
				BackgroundColor = Color.FromArgb("#31454F"),
				StrokeShape = new RoundRectangle { CornerRadius = 14 },
				Content = initials
			};

			var badge = new Border
			{
				WidthRequest = 10,
				HeightRequest = 10,
				StrokeThickness = 0,
				HorizontalOptions = LayoutOptions.End,
				VerticalOptions = LayoutOptions.Start,
				StrokeShape = new RoundRectangle { CornerRadius = 5 }
			};
			badge.SetBinding(VisualElement.BackgroundColorProperty, nameof(MediaStoryCard.AccentColor));

			return new Grid
			{
				WidthRequest = 44,
				HeightRequest = 34,
				Children =
				{
					avatar,
					badge
				}
			};
		});
	}

	DataTemplate CreatePreviewPillTemplate()
	{
		return new DataTemplate(() =>
		{
			var code = new Label
			{
				FontSize = 11,
				FontAttributes = FontAttributes.Bold,
				TextColor = Colors.White,
				VerticalTextAlignment = TextAlignment.Center
			};
			code.SetBinding(Label.TextProperty, nameof(MediaStoryCard.PreviewCode));

			var badge = new Border
			{
				WidthRequest = 12,
				HeightRequest = 12,
				StrokeThickness = 0,
				StrokeShape = new RoundRectangle { CornerRadius = 6 },
				VerticalOptions = LayoutOptions.Center
			};
			badge.SetBinding(VisualElement.BackgroundColorProperty, nameof(MediaStoryCard.AccentColor));

			return new Border
			{
				WidthRequest = 66,
				HeightRequest = 28,
				StrokeThickness = 0,
				BackgroundColor = Color.FromArgb("#24313A"),
				StrokeShape = new RoundRectangle { CornerRadius = 14 },
				Padding = new Thickness(10, 0),
				Content = new HorizontalStackLayout
				{
					Spacing = 8,
					VerticalOptions = LayoutOptions.Center,
					Children =
					{
						code,
						badge
					}
				}
			};
		});
	}

	static View CreateStoryCardView()
	{
		var title = new Label
		{
			FontSize = 18,
			FontAttributes = FontAttributes.Bold,
			TextColor = Colors.White
		};
		title.SetBinding(Label.TextProperty, nameof(MediaStoryCard.Title));

		var subtitle = new Label
		{
			FontSize = 13,
			TextColor = Color.FromArgb("#DDEBE4")
		};
		subtitle.SetBinding(Label.TextProperty, nameof(MediaStoryCard.Subtitle));

		var creator = new Label
		{
			FontSize = 13,
			TextColor = Colors.White
		};
		creator.SetBinding(Label.TextProperty, nameof(MediaStoryCard.Creator));

		var duration = new Label
		{
			FontSize = 12,
			TextColor = Color.FromArgb("#DDEBE4")
		};
		duration.SetBinding(Label.TextProperty, nameof(MediaStoryCard.DurationText));

		var status = new Label
		{
			FontSize = 12,
			TextColor = Colors.White,
			BackgroundColor = Color.FromArgb("#2C8C73"),
			Padding = new Thickness(8, 3),
			HorizontalOptions = LayoutOptions.Start
		};
		status.SetBinding(Label.TextProperty, nameof(MediaStoryCard.Status));

		var accent = new BoxView
		{
			WidthRequest = 6,
			CornerRadius = 3,
			HorizontalOptions = LayoutOptions.Start
		};
		accent.SetBinding(BoxView.ColorProperty, nameof(MediaStoryCard.AccentColor));

		var textStack = new VerticalStackLayout
		{
			Spacing = 10,
			Children =
			{
				title,
				subtitle,
				creator,
				duration,
				status
			}
		};

		var layoutGrid = new Grid
		{
			ColumnDefinitions =
			{
				new ColumnDefinition(GridLength.Auto),
				new ColumnDefinition(GridLength.Star)
			},
			ColumnSpacing = 12
		};
		layoutGrid.Add(accent);
		layoutGrid.Add(textStack, 1, 0);

		return new Border
		{
			StrokeThickness = 0,
			BackgroundColor = Color.FromArgb("#172026"),
			StrokeShape = new RoundRectangle { CornerRadius = 10 },
			Margin = new Thickness(6, 0),
			Padding = new Thickness(18),
			Content = layoutGrid
		};
	}

	static View CreatePayloadPanel(View explanation)
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
						Text = "Payload model",
						FontAttributes = FontAttributes.Bold,
						FontSize = 13,
						TextColor = Color.FromArgb("#25312D")
					},
					explanation
				}
			}
		};
	}

	static string FormatBytes(long bytes)
	{
		if (bytes >= 1024L * 1024L)
			return $"{bytes / 1024d / 1024d:0.0} MB";

		if (bytes >= 1024L)
			return $"{bytes / 1024d:0.0} KB";

		return $"{bytes} B";
	}

	sealed record LiveLayoutGeneration(
		int GenerationIndex,
		string TemplateName,
		Layout Layout,
		IReadOnlyList<RetainedPayloadBehavior> PayloadBehaviors);
}
