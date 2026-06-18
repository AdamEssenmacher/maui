using System.Diagnostics;
using Microsoft.Maui;

namespace Maui32726Repro;

public sealed class MainPage : ContentPage
{
	static string ResultFilePath => Path.Combine(
		Microsoft.Maui.Storage.FileSystem.AppDataDirectory,
		"maui32726-repro-result.txt");
	static bool AutoRunOnStartup =>
		string.Equals(Environment.GetEnvironmentVariable("MAUI32726_AUTORUN"), "1", StringComparison.OrdinalIgnoreCase);
	static bool ExitAfterResult =>
		string.Equals(Environment.GetEnvironmentVariable("MAUI32726_EXIT_AFTER_RESULT"), "1", StringComparison.OrdinalIgnoreCase);

	readonly CollectionView _catalogView;
	readonly GridItemsLayout _itemsLayout;
	readonly ContentView _workspaceSlot;
	readonly View _reportsPlaceholder;
	readonly Label _statusLabel;
	readonly Label _detailsLabel;
	readonly Label _metricsLabel;
	readonly Button _restoreButton;
	readonly Button _narrowButton;
	readonly Button _wideButton;
	readonly CachedWorkspaceHost _cachedHost = new();
	bool _autoRunStarted;
	bool _isRunning;
	double _currentWorkspaceWidth = 560;

	public MainPage()
	{
		Title = "Adaptive Catalog";

		_itemsLayout = new GridItemsLayout(2, ItemsLayoutOrientation.Vertical)
		{
			HorizontalItemSpacing = 10,
			VerticalItemSpacing = 10
		};

		_catalogView = new CollectionView
		{
			AutomationId = "CatalogCollectionView",
			ItemsLayout = _itemsLayout,
			ItemsSource = CatalogItem.CreateSampleData(),
			ItemTemplate = new DataTemplate(CreateCatalogTile)
		};

		_workspaceSlot = new ContentView
		{
			AutomationId = "WorkspaceSlot",
			Content = _catalogView
		};

		_reportsPlaceholder = new Grid
		{
			BackgroundColor = Color.FromArgb("#F3F6F8"),
			Padding = 18,
			Children =
			{
				new Label
				{
					Text = "Orders workspace",
					FontSize = 18,
					FontAttributes = FontAttributes.Bold,
					TextColor = Color.FromArgb("#1D2733")
				}
			}
		};

		_statusLabel = new Label
		{
			AutomationId = "ScenarioStatus",
			FontAttributes = FontAttributes.Bold,
			Text = "Catalog workspace loading..."
		};

		_detailsLabel = new Label
		{
			AutomationId = "ScenarioDetails",
			FontSize = 12,
			LineBreakMode = LineBreakMode.WordWrap,
			Text = "Click Restore Catalog to shelve and restore the cached catalog workspace, then apply a wider adaptive layout."
		};

		_metricsLabel = new Label
		{
			AutomationId = "CatalogMetrics",
			FontSize = 12,
			TextColor = Color.FromArgb("#4B5563"),
			Text = GetMetricsText("initial")
		};

		_restoreButton = new Button
		{
			AutomationId = "RestoreWorkspaceButton",
			Text = "Restore Catalog"
		};
		_restoreButton.Clicked += async (_, _) => await RunWorkspaceRestoreAsync("button");

		_narrowButton = new Button
		{
			AutomationId = "NarrowWorkspaceButton",
			Text = "Narrow"
		};
		_narrowButton.Clicked += (_, _) => ApplyAdaptiveCatalogWidth(520, "manual narrow resize");

		_wideButton = new Button
		{
			AutomationId = "WideWorkspaceButton",
			Text = "Wide"
		};
		_wideButton.Clicked += (_, _) => ApplyAdaptiveCatalogWidth(980, "manual wide resize");

		Content = new Grid
		{
			RowDefinitions =
			{
				new RowDefinition(GridLength.Auto),
				new RowDefinition(GridLength.Auto),
				new RowDefinition(GridLength.Auto),
				new RowDefinition(GridLength.Star),
				new RowDefinition(GridLength.Auto)
			},
			Padding = 24,
			RowSpacing = 12,
			BackgroundColor = Color.FromArgb("#FFFFFF"),
			Children =
			{
				new Grid
				{
					ColumnDefinitions =
					{
						new ColumnDefinition(GridLength.Star),
						new ColumnDefinition(GridLength.Auto)
					},
					Children =
					{
						new VerticalStackLayout
						{
							Spacing = 2,
							Children =
							{
								new Label
								{
									Text = "Inventory Catalog",
									FontSize = 24,
									FontAttributes = FontAttributes.Bold,
									TextColor = Color.FromArgb("#17202A")
								},
								new Label
								{
									Text = "Adaptive grid workspace",
									FontSize = 13,
									TextColor = Color.FromArgb("#5B6673")
								}
							}
						}.GridColumn(0),
						_metricsLabel.GridColumn(1)
					}
				}.GridRow(0),
				_statusLabel.GridRow(1),
				new HorizontalStackLayout
				{
					Spacing = 8,
					Children =
					{
						_restoreButton,
						_narrowButton,
						_wideButton
					}
				}.GridRow(2),
				_workspaceSlot.GridRow(3),
				_detailsLabel.GridRow(4)
			}
		};

		WriteResult("STARTED", $"Adaptive catalog constructed. Result file: {ResultFilePath}");
		Loaded += OnLoaded;
		SizeChanged += (_, _) =>
		{
			if (!_isRunning && Width > 0)
			{
				ApplyAdaptiveCatalogWidth(Width, "window size changed");
			}
		};
	}

	static View CreateCatalogTile()
	{
		var name = new Label
		{
			FontAttributes = FontAttributes.Bold,
			FontSize = 14,
			TextColor = Color.FromArgb("#16202A"),
			LineBreakMode = LineBreakMode.TailTruncation
		};

		var category = new Label
		{
			FontSize = 12,
			TextColor = Color.FromArgb("#5B6673")
		};

		var stock = new Label
		{
			FontSize = 12,
			TextColor = Color.FromArgb("#0F6B4F")
		};

		var price = new Label
		{
			FontAttributes = FontAttributes.Bold,
			FontSize = 13,
			TextColor = Color.FromArgb("#17202A"),
			HorizontalOptions = LayoutOptions.End
		};

		var tile = new Border
		{
			Padding = 12,
			BackgroundColor = Color.FromArgb("#F8FAFC"),
			Stroke = Color.FromArgb("#D6DEE6"),
			StrokeThickness = 1,
			Content = new Grid
			{
				RowDefinitions =
				{
					new RowDefinition(GridLength.Auto),
					new RowDefinition(GridLength.Auto),
					new RowDefinition(GridLength.Auto)
				},
				RowSpacing = 6,
				Children =
				{
					name.GridRow(0),
					category.GridRow(1),
					new Grid
					{
						ColumnDefinitions =
						{
							new ColumnDefinition(GridLength.Star),
							new ColumnDefinition(GridLength.Auto)
						},
						Children =
						{
							stock.GridColumn(0),
							price.GridColumn(1)
						}
					}.GridRow(2)
				}
			}
		};

		tile.BindingContextChanged += (_, _) =>
		{
			if (tile.BindingContext is not CatalogItem item)
			{
				return;
			}

			name.Text = item.Name;
			category.Text = item.Category;
			stock.Text = item.StockText;
			price.Text = item.PriceText;
		};

		return tile;
	}

	void OnLoaded(object? sender, EventArgs e)
	{
		if (_autoRunStarted)
		{
			return;
		}

		_autoRunStarted = true;
		if (AutoRunOnStartup)
		{
			Dispatcher.DispatchDelayed(TimeSpan.FromMilliseconds(350), async () => await RunWhenHandlerIsReadyAsync());
			return;
		}

		_statusLabel.Text = "Ready: click Restore Catalog.";
		_detailsLabel.Text = "Manual mode is enabled. Use Restore Catalog to run the cached-workspace restore and adaptive resize flow.";
	}

	async Task RunWhenHandlerIsReadyAsync()
	{
		for (var attempt = 1; attempt <= 40; attempt++)
		{
			if (_catalogView.Handler is not null)
			{
				await RunWorkspaceRestoreAsync($"automatic after {attempt} handler check(s)");
				return;
			}

			await Task.Delay(250);
		}

		_statusLabel.Text = "Unexpected exception: catalog handler was not created.";
		_detailsLabel.Text = "The CollectionView handler was still null after waiting 10 seconds.";
		WriteResult(_statusLabel.Text, _detailsLabel.Text);
		ScheduleExit();
	}

	async Task RunWorkspaceRestoreAsync(string trigger)
	{
		if (_isRunning)
		{
			return;
		}

		_isRunning = true;
		SetButtonsEnabled(false);
		_statusLabel.Text = $"Restoring cached catalog workspace ({trigger})...";
		_detailsLabel.Text = "The catalog is shelved while another workspace is shown, then restored and resized to a wider adaptive layout.";

		try
		{
			await Task.Yield();
			ApplyAdaptiveCatalogWidth(560, "catalog opened in compact width");
			await Task.Delay(100);

			_statusLabel.Text = "Orders workspace active; catalog native view shelved.";
			_workspaceSlot.Content = _reportsPlaceholder;
			_cachedHost.Shelve(_catalogView);
			await Task.Delay(150);

			_statusLabel.Text = "Catalog workspace restored from cache.";
			_cachedHost.Restore(_catalogView);
			_workspaceSlot.Content = _catalogView;
			await Task.Delay(150);

			_statusLabel.Text = "Applying wider adaptive catalog layout...";
			ApplyAdaptiveCatalogWidth(980, "restored into wider window");

			_statusLabel.Text = "PASS after fix: adaptive catalog restored and resized without exception.";
			_detailsLabel.Text = "The cached catalog returned to the workspace and changed grid span without crashing.";
			WriteResult(_statusLabel.Text, BuildResultDetails("No exception was thrown."));
			Console.WriteLine(_statusLabel.Text);
			await ShowManualResultAlertAsync(
				"Not reproduced",
				"The cached catalog restored and resized without throwing. Check the result file for details.");
		}
		catch (NullReferenceException ex)
		{
			_statusLabel.Text = "REPRODUCED: adaptive catalog crashed after restore.";
			_detailsLabel.Text = ex.ToString();
			WriteResult(_statusLabel.Text, BuildResultDetails(ex.ToString()));
			Console.WriteLine(_statusLabel.Text);
			Console.WriteLine(ex);
			Debug.WriteLine(ex);
			await ShowManualResultAlertAsync(
				"Reproduced #32726",
				"The cached adaptive catalog hit NullReferenceException after restore and resize. Check the result file for the stack trace.");
		}
		catch (Exception ex)
		{
			_statusLabel.Text = $"Unexpected exception: {ex.GetType().Name}";
			_detailsLabel.Text = ex.ToString();
			WriteResult(_statusLabel.Text, BuildResultDetails(ex.ToString()));
			Console.WriteLine(_statusLabel.Text);
			Console.WriteLine(ex);
			Debug.WriteLine(ex);
			await ShowManualResultAlertAsync(
				"Unexpected exception",
				$"The repro hit {ex.GetType().Name}. Check the result file for the stack trace.");
		}
		finally
		{
			SetButtonsEnabled(true);
			_isRunning = false;
			ScheduleExit();
		}
	}

	void ApplyAdaptiveCatalogWidth(double width, string reason)
	{
		_currentWorkspaceWidth = width;
		var newSpan = width switch
		{
			>= 920 => 4,
			>= 700 => 3,
			_ => 2
		};

		if (_itemsLayout.Span != newSpan)
		{
			_itemsLayout.Span = newSpan;
		}

		_metricsLabel.Text = GetMetricsText(reason);
	}

	string GetMetricsText(string reason) =>
		$"{_itemsLayout.Span} columns - {_currentWorkspaceWidth:0}px - {reason}";

	string BuildResultDetails(string details) =>
		$"""
		Scenario: cached adaptive catalog workspace.
		Handler: {_catalogView.Handler?.GetType().FullName ?? "none"}
		Width: {_currentWorkspaceWidth:0}
		Span: {_itemsLayout.Span}

		{details}
		""";

	void SetButtonsEnabled(bool enabled)
	{
		_restoreButton.IsEnabled = enabled;
		_narrowButton.IsEnabled = enabled;
		_wideButton.IsEnabled = enabled;
	}

	async Task ShowManualResultAlertAsync(string title, string message)
	{
		if (!AutoRunOnStartup)
		{
			await DisplayAlertAsync(title, message, "OK");
		}
	}

	static void WriteResult(string status, string details)
	{
		try
		{
			Directory.CreateDirectory(Path.GetDirectoryName(ResultFilePath)!);
			File.WriteAllText(
				ResultFilePath,
				$"""
				{DateTimeOffset.Now:O}
				{status}

				{details}
				""");
		}
		catch (Exception ex)
		{
			Console.WriteLine($"Failed to write result file '{ResultFilePath}': {ex}");
			Debug.WriteLine(ex);
		}
	}

	void ScheduleExit()
	{
		if (ExitAfterResult)
		{
			Dispatcher.DispatchDelayed(TimeSpan.FromSeconds(2), () => Application.Current?.Quit());
		}
	}
}

sealed class CachedWorkspaceHost
{
	IElementHandler? _catalogHandler;
	IMauiContext? _mauiContext;

	public void Shelve(CollectionView catalogView)
	{
		_catalogHandler = catalogView.Handler
			?? throw new InvalidOperationException("Catalog handler was not created yet.");
		_mauiContext = _catalogHandler.MauiContext
			?? throw new InvalidOperationException("Catalog handler has no MauiContext.");

		_catalogHandler.DisconnectHandler();
	}

	public void Restore(CollectionView catalogView)
	{
		if (_catalogHandler is null || _mauiContext is null)
		{
			throw new InvalidOperationException("Catalog workspace has not been shelved.");
		}

		_catalogHandler.SetMauiContext(_mauiContext);
		_catalogHandler.SetVirtualView(catalogView);
	}
}

sealed record CatalogItem(string Name, string Category, int Stock, decimal Price)
{
	public string StockText => $"{Stock} in stock";
	public string PriceText => Price.ToString("C0");

	public static CatalogItem[] CreateSampleData() =>
	[
		new("Trail Jacket", "Outerwear", 38, 128),
		new("Merino Crew", "Base layers", 64, 72),
		new("Commuter Pack", "Bags", 22, 96),
		new("Rain Shell", "Outerwear", 17, 154),
		new("Thermal Flask", "Accessories", 113, 28),
		new("Canvas Tote", "Bags", 49, 36),
		new("Travel Hoodie", "Mid layers", 41, 88),
		new("Wool Beanie", "Accessories", 76, 32),
		new("Deck Shoe", "Footwear", 29, 118),
		new("Linen Shirt", "Tops", 57, 84),
		new("Field Pant", "Bottoms", 33, 102),
		new("Day Runner", "Footwear", 25, 134)
	];
}

static class GridExtensions
{
	public static T GridRow<T>(this T view, int row)
		where T : BindableObject
	{
		Grid.SetRow(view, row);
		return view;
	}

	public static T GridColumn<T>(this T view, int column)
		where T : BindableObject
	{
		Grid.SetColumn(view, column);
		return view;
	}
}
