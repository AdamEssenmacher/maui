using Microsoft.Maui.Storage;
using Microsoft.Maui.Controls.Shapes;
using UIKit;

namespace CarouselViewDetachedRefreshRepro;

public sealed class CatalogPage : ContentPage
{
	const string ResultFileName = "carousel-repro-result.txt";

	readonly CatalogViewModel _viewModel;
	readonly CarouselView _carousel;
	readonly IndicatorView _indicator;
	readonly Button _runButton;
	readonly Border _resultPanel;
	readonly Label _resultLabel;
	readonly Label _selectionLabel;
	readonly Label _eventLogLabel;
	readonly List<string> _events = [];

	RunPhase _phase;
	bool _autoRunStarted;
	bool _initialStateReady;
	bool _detachedObserved;
	string? _error;

	public CatalogPage(CatalogViewModel viewModel)
	{
		_viewModel = viewModel;
		BindingContext = viewModel;
		Title = "Recommended products";
		BackgroundColor = Color.FromArgb("#F5F7FA");

		var title = new Label
		{
			Text = "Travel recommendations",
			FontSize = 28,
			FontAttributes = FontAttributes.Bold,
			TextColor = Color.FromArgb("#172B4D")
		};

		var explanation = new Label
		{
			Text = "A common flow: open filters, replace the catalog, select the best recommendation, then return.",
			FontSize = 14,
			TextColor = Color.FromArgb("#42526E")
		};

		_carousel = new CarouselView
		{
			AutomationId = "ProductCarousel",
			HeightRequest = 245,
			Loop = false,
			IsBounceEnabled = false,
			IsScrollAnimated = false,
			ItemsLayout = new LinearItemsLayout(ItemsLayoutOrientation.Horizontal)
			{
				SnapPointsType = SnapPointsType.MandatorySingle,
				SnapPointsAlignment = SnapPointsAlignment.Center,
				ItemSpacing = 12
			},
			ItemTemplate = CreateProductTemplate()
		};
		_carousel.SetBinding(ItemsView.ItemsSourceProperty, static (CatalogViewModel viewModel) => viewModel.Products);
		_carousel.SetBinding(CarouselView.CurrentItemProperty, static (CatalogViewModel viewModel) => viewModel.SelectedProduct, BindingMode.TwoWay);
		_carousel.SetBinding(CarouselView.PositionProperty, static (CatalogViewModel viewModel) => viewModel.Position, BindingMode.TwoWay);

		_indicator = new IndicatorView
		{
			AutomationId = "ProductIndicator",
			HorizontalOptions = LayoutOptions.Center,
			IndicatorColor = Color.FromArgb("#B3BAC5"),
			SelectedIndicatorColor = Color.FromArgb("#0052CC"),
			IndicatorSize = 11
		};
		_carousel.IndicatorView = _indicator;

		_selectionLabel = new Label
		{
			AutomationId = "SelectionSummary",
			Text = "Preparing initial catalog…",
			FontSize = 14,
			TextColor = Color.FromArgb("#253858")
		};

		_runButton = new Button
		{
			AutomationId = "RunRefreshFlow",
			Text = "Run filter + refresh flow",
			BackgroundColor = Color.FromArgb("#0052CC"),
			TextColor = Colors.White,
			CornerRadius = 10
		};
		_runButton.Clicked += async (_, _) => await StartScenarioSafelyAsync();

		_resultLabel = new Label
		{
			AutomationId = "ReproResult",
			Text = AutoRunSettings.IsEnabled ? "Autorun scheduled…" : "Ready for a manual run.",
			FontSize = 15,
			TextColor = Color.FromArgb("#253858")
		};

		_resultPanel = new Border
		{
			AutomationId = "ResultPanel",
			Padding = 14,
			BackgroundColor = Color.FromArgb("#E9F2FF"),
			Stroke = Color.FromArgb("#4C9AFF"),
			StrokeThickness = 1,
			StrokeShape = new RoundRectangle { CornerRadius = 12 },
			Content = _resultLabel
		};

		_eventLogLabel = new Label
		{
			AutomationId = "EventLog",
			FontFamily = "Courier",
			FontSize = 11,
			TextColor = Color.FromArgb("#5E6C84"),
			LineBreakMode = LineBreakMode.WordWrap
		};

		var content = new VerticalStackLayout
		{
			Padding = new Thickness(20, 18),
			Spacing = 12,
			Children =
			{
				title,
				explanation,
				_carousel,
				_indicator,
				_selectionLabel,
				_runButton,
				_resultPanel,
				new Label
				{
					Text = "Why this matters: checkout, booking, or detail commands usually act on the ViewModel's selected item.",
					FontSize = 12,
					TextColor = Color.FromArgb("#6B778C")
				},
				_eventLogLabel
			}
		};

		Content = new ScrollView { Content = content };
		UpdateSelectionSummary();
	}

	protected override void OnAppearing()
	{
		base.OnAppearing();

		if (_phase == RunPhase.Returning)
		{
			_phase = RunPhase.Verifying;
			_ = VerifyAfterReturnSafelyAsync();
			return;
		}

		if (AutoRunSettings.IsEnabled && !_autoRunStarted && _phase == RunPhase.Idle)
		{
			_autoRunStarted = true;
			_ = StartScenarioSafelyAsync();
		}
	}

	public bool IsNativeCarouselDetached =>
		_carousel.Handler?.PlatformView is UIView platformView && platformView.Window is null;

	public void RecordDetachedRefresh(bool detachedObserved)
	{
		_detachedObserved = detachedObserved;
		Log($"Native carousel detached: {detachedObserved}");
		_viewModel.ApplyTravelFilter();
		Log($"Filter applied off-screen: ItemsSource=B, selected=B1, Position left at {_viewModel.Position}");
		UpdateSelectionSummary();
	}

	public void RecordFilterError(Exception exception)
	{
		_error = exception.ToString();
		Log($"FILTER ERROR: {exception.GetType().Name}: {exception.Message}");
	}

	public void PrepareForReturn() => _phase = RunPhase.Returning;

	async Task StartScenarioSafelyAsync()
	{
		try
		{
			await StartScenarioAsync();
		}
		catch (Exception exception)
		{
			await FailWithExceptionAsync(exception);
		}
	}

	async Task StartScenarioAsync()
	{
		if (_phase is RunPhase.Preparing or RunPhase.Away or RunPhase.Returning or RunPhase.Verifying)
			return;

		_phase = RunPhase.Preparing;
		_runButton.IsEnabled = false;
		_error = null;
		_detachedObserved = false;
		_initialStateReady = false;
		_events.Clear();
		_resultPanel.BackgroundColor = Color.FromArgb("#E9F2FF");
		_resultPanel.Stroke = Color.FromArgb("#4C9AFF");
		_resultLabel.Text = "Preparing product 4 before navigation…";

		_viewModel.ResetForRun();
		Log("Initial catalog A requested at Position 3 / CurrentItem A3");
		_initialStateReady = await WaitForAsync(
			() => _carousel.Position == 3 && ProductId(_carousel.CurrentItem) == "A3",
			TimeSpan.FromSeconds(5));
		Log($"Initial state synchronized: {_initialStateReady} (Position={_carousel.Position}, CurrentItem={ProductId(_carousel.CurrentItem)})");
		UpdateSelectionSummary();

		if (!_initialStateReady)
			throw new InvalidOperationException("The initial CarouselView state did not synchronize to A3 / Position 3.");

		_phase = RunPhase.Away;
		Log("Navigating to the normal filter page");
		await Navigation.PushAsync(new FilterPage(this), animated: true);
	}

	async Task VerifyAfterReturnSafelyAsync()
	{
		try
		{
			await Task.Delay(1200);
			await VerifyAfterReturnAsync();
		}
		catch (Exception exception)
		{
			await FailWithExceptionAsync(exception);
		}
	}

	async Task VerifyAfterReturnAsync()
	{
		var expected = _viewModel.ExpectedProductAfterRefresh;
		var carouselCurrent = _carousel.CurrentItem as Product;
		var viewModelCurrent = _viewModel.SelectedProduct;
		var visibleProducts = _carousel.VisibleViews
			.Select(view => view.BindingContext as Product)
			.Where(product => product is not null)
			.Select(product => product!.Id)
			.Distinct()
			.ToArray();

		var baselinePassed = _initialStateReady &&
			_detachedObserved &&
			expected?.Id == "B1" &&
			_carousel.Position == 1 &&
			_indicator.Position == 1 &&
			carouselCurrent?.Id == "B1" &&
			viewModelCurrent?.Id == "B1";

		var regressionReproduced = _initialStateReady &&
			_detachedObserved &&
			expected?.Id == "B1" &&
			_carousel.Position == 3 &&
			_indicator.Position == 3 &&
			carouselCurrent?.Id == "B3" &&
			viewModelCurrent?.Id == "B3";

		var outcome = _error is not null
			? ReproOutcome.HarnessError
			: baselinePassed
				? ReproOutcome.BaselinePass
				: regressionReproduced
					? ReproOutcome.RegressionReproduced
					: ReproOutcome.Inconclusive;

		Log($"Returned: Position={_carousel.Position}, Indicator={_indicator.Position}, Carousel.CurrentItem={carouselCurrent?.Id ?? "null"}");
		Log($"ViewModel.SelectedProduct={viewModelCurrent?.Id ?? "null"}; visible=[{string.Join(",", visibleProducts)}]");
		Log($"Outcome={OutcomeName(outcome)}");

		var headline = outcome switch
		{
			ReproOutcome.BaselinePass => "BASELINE BEHAVIOR: CORRECT ITEM",
			ReproOutcome.RegressionReproduced => "REGRESSION REPRODUCED: WRONG ITEM",
			ReproOutcome.Inconclusive => "INCONCLUSIVE: UNEXPECTED STATE",
			_ => "REPRO HARNESS ERROR"
		};
		var actionTarget = viewModelCurrent is null ? "none" : $"{viewModelCurrent.Name} ({viewModelCurrent.Id})";
		_resultLabel.Text =
			$"{headline}\n\n" +
			$"Expected recommendation: City Backpack (B1), position 1\n" +
			$"Displayed/bound item: {carouselCurrent?.Name ?? "null"} ({carouselCurrent?.Id ?? "null"}), position {_carousel.Position}\n" +
			$"Indicator position: {_indicator.Position}\n" +
			$"A real checkout action would now target: {actionTarget}" +
			(outcome == ReproOutcome.Inconclusive ? "\n\nThis state matches neither the exact parent nor affected signature." : string.Empty) +
			(outcome == ReproOutcome.HarnessError ? $"\n\n{_error}" : string.Empty);

		_resultPanel.BackgroundColor = Color.FromArgb(outcome switch
		{
			ReproOutcome.BaselinePass => "#E3FCEF",
			ReproOutcome.RegressionReproduced => "#FFEBE6",
			_ => "#FFF4E5"
		});
		_resultPanel.Stroke = Color.FromArgb(outcome switch
		{
			ReproOutcome.BaselinePass => "#00875A",
			ReproOutcome.RegressionReproduced => "#DE350B",
			_ => "#FF991F"
		});
		UpdateSelectionSummary();

		await WriteResultAsync(outcome, expected, carouselCurrent, viewModelCurrent, visibleProducts);
		_phase = RunPhase.Completed;
		_runButton.Text = "Run the flow again";
		_runButton.IsEnabled = true;
	}

	async Task FailWithExceptionAsync(Exception exception)
	{
		_error = exception.ToString();
		Log($"ERROR: {exception.GetType().Name}: {exception.Message}");
		_resultLabel.Text = $"REPRO HARNESS ERROR\n\n{exception.Message}";
		_resultPanel.BackgroundColor = Color.FromArgb("#FFEBE6");
		_resultPanel.Stroke = Color.FromArgb("#DE350B");
		_phase = RunPhase.Completed;
		_runButton.IsEnabled = true;
		await WriteResultAsync(ReproOutcome.HarnessError, _viewModel.ExpectedProductAfterRefresh, _carousel.CurrentItem as Product, _viewModel.SelectedProduct, []);
	}

	async Task WriteResultAsync(
		ReproOutcome outcome,
		Product? expected,
		Product? carouselCurrent,
		Product? viewModelCurrent,
		IReadOnlyList<string> visibleProducts)
	{
		var result = string.Join(Environment.NewLine,
		[
			$"timestamp_utc={DateTimeOffset.UtcNow:O}",
			$"build_label={AutoRunSettings.BuildLabel}",
			$"result={OutcomeName(outcome)}",
			$"initial_state_ready={_initialStateReady}",
			$"native_carousel_detached={_detachedObserved}",
			$"expected_product={expected?.Id ?? "null"}",
			$"carousel_position={_carousel.Position}",
			$"indicator_position={_indicator.Position}",
			$"carousel_current_item={carouselCurrent?.Id ?? "null"}",
			$"viewmodel_selected_product={viewModelCurrent?.Id ?? "null"}",
			$"visible_products={string.Join(",", visibleProducts)}",
			$"error={_error ?? "none"}",
			"events:",
			.. _events.Select(entry => $"  {entry}")
		]);

		var resultPath = System.IO.Path.Combine(FileSystem.AppDataDirectory, ResultFileName);
		await File.WriteAllTextAsync(resultPath, result);
		Console.WriteLine($"CAROUSEL_REPRO_RESULT|{OutcomeName(outcome)}|{resultPath}");
		Console.WriteLine(result);
	}

	void UpdateSelectionSummary()
	{
		_selectionLabel.Text =
			$"Bound selection: {_viewModel.SelectedProduct?.Name ?? "none"} ({_viewModel.SelectedProduct?.Id ?? "null"})  •  " +
			$"Position: {_viewModel.Position}";
	}

	void Log(string message)
	{
		var entry = $"{DateTimeOffset.Now:HH:mm:ss.fff} {message}";
		_events.Add(entry);
		_eventLogLabel.Text = string.Join(Environment.NewLine, _events.TakeLast(6));
		Console.WriteLine($"CAROUSEL_REPRO_EVENT|{entry}");
	}

	static string? ProductId(object? item) => (item as Product)?.Id;

	static string OutcomeName(ReproOutcome outcome) => outcome switch
	{
		ReproOutcome.BaselinePass => "BASELINE_PASS",
		ReproOutcome.RegressionReproduced => "REGRESSION_REPRODUCED",
		ReproOutcome.Inconclusive => "INCONCLUSIVE",
		_ => "HARNESS_ERROR"
	};

	static async Task<bool> WaitForAsync(Func<bool> condition, TimeSpan timeout)
	{
		var deadline = DateTimeOffset.UtcNow + timeout;
		while (DateTimeOffset.UtcNow < deadline)
		{
			if (condition())
				return true;

			await Task.Delay(100);
		}

		return condition();
	}

	static DataTemplate CreateProductTemplate()
	{
		return new DataTemplate(() =>
		{
			var name = new Label
			{
				FontSize = 25,
				FontAttributes = FontAttributes.Bold,
				HorizontalTextAlignment = TextAlignment.Center,
				TextColor = Color.FromArgb("#172B4D")
			};
			name.SetBinding(Label.TextProperty, static (Product product) => product.Name);

			var price = new Label
			{
				FontSize = 18,
				HorizontalTextAlignment = TextAlignment.Center,
				TextColor = Color.FromArgb("#42526E")
			};
			price.SetBinding(Label.TextProperty, static (Product product) => product.Price);

			var identifier = new Label
			{
				FontSize = 12,
				HorizontalTextAlignment = TextAlignment.Center,
				TextColor = Color.FromArgb("#6B778C")
			};
			identifier.SetBinding(Label.TextProperty, static (Product product) => product.Id, stringFormat: "Catalog item {0}");

			var card = new Border
			{
				Margin = new Thickness(6, 4),
				Padding = new Thickness(20),
				Stroke = Color.FromArgb("#A5ADBA"),
				StrokeThickness = 1,
				StrokeShape = new RoundRectangle { CornerRadius = 20 },
				Content = new VerticalStackLayout
				{
					Spacing = 12,
					VerticalOptions = LayoutOptions.Center,
					Children = { name, price, identifier }
				}
			};
			card.SetBinding(BackgroundColorProperty, static (Product product) => product.CardColor);
			card.SetBinding(AutomationIdProperty, static (Product product) => product.Id, stringFormat: "ProductCard_{0}");
			return card;
		});
	}

	enum RunPhase
	{
		Idle,
		Preparing,
		Away,
		Returning,
		Verifying,
		Completed
	}

	enum ReproOutcome
	{
		BaselinePass,
		RegressionReproduced,
		Inconclusive,
		HarnessError
	}
}
