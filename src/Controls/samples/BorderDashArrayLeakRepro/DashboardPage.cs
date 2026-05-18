namespace BorderDashArrayLeakRepro;

public sealed class DashboardPage : ContentPage
{
	readonly Entry _pagesEntry;
	readonly Entry _cardsEntry;
	readonly Entry _itemPayloadEntry;
	readonly Entry _pagePayloadEntry;
	readonly Entry _dwellEntry;
	readonly Button _runLeakButton;
	readonly Button _runControlButton;
	readonly Button _runMitigationButton;
	readonly Button _stopButton;
	readonly ProgressBar _progress;
	readonly Label _statusLabel;
	readonly Label _summaryLabel;
	CancellationTokenSource? _runCancellation;
	MemorySnapshot _baseline = MemorySnapshot.Empty;

	public DashboardPage()
	{
		Title = "Border Dash Leak Repro";
		BackgroundColor = Colors.White;

		_pagesEntry = CreateEntry("20");
		_cardsEntry = CreateEntry("64");
		_itemPayloadEntry = CreateEntry("96");
		_pagePayloadEntry = CreateEntry("3");
		_dwellEntry = CreateEntry("100");

		_runLeakButton = CreateButton("Run shared resource leak", () => RunAsync(ReproMode.SharedAppResourceDashArray));
		_runControlButton = CreateButton("Run control", () => RunAsync(ReproMode.SolidBorderControl));
		_runMitigationButton = CreateButton("Run per-border mitigation", () => RunAsync(ReproMode.PerBorderDashArrayMitigation));
		_stopButton = CreateButton("Stop", StopRun);
		_stopButton.IsEnabled = false;

		_progress = new ProgressBar
		{
			Progress = 0,
			HeightRequest = 6,
			ProgressColor = Color.FromArgb("#2563EB")
		};

		_statusLabel = new Label
		{
			Text = "Ready. Run the control first, then the shared resource scenario.",
			TextColor = Color.FromArgb("#1E293B"),
			FontSize = 14
		};

		_summaryLabel = new Label
		{
			Text = "Each run pushes realistic CollectionView pages, pops them, forces full GC, and counts weak references. The leaky run uses one AppResource DoubleCollection as every card Border.StrokeDashArray.",
			TextColor = Color.FromArgb("#1E293B"),
			FontFamily = GetMonospaceFontFamily(),
			FontSize = 13,
			LineBreakMode = LineBreakMode.WordWrap
		};

		Content = new ScrollView
		{
			Content = new VerticalStackLayout
			{
				Padding = new Thickness(18, 18, 18, 28),
				Spacing = 16,
				Children =
				{
					new Label
					{
						Text = "Border StrokeDashArray retention",
						FontSize = 22,
						FontAttributes = FontAttributes.Bold,
						TextColor = Color.FromArgb("#0F172A")
					},
					new Label
					{
						Text = "A shared dashed border resource is a normal app pattern. This repro shows how it can retain realized card Borders and, through page-level event handlers, the whole page, CollectionView, and view models.",
						FontSize = 14,
						TextColor = Color.FromArgb("#475569")
					},
					CreateSettingsGrid(),
					CreateButtonGrid(),
					_progress,
					_statusLabel,
					_summaryLabel
				}
			}
		};
	}

	protected override async void OnAppearing()
	{
		base.OnAppearing();
		await DeviceProofRunner.RunIfRequestedAsync(_statusLabel, _summaryLabel);
	}

	Grid CreateSettingsGrid()
	{
		var grid = new Grid
		{
			ColumnDefinitions =
			{
				new ColumnDefinition(GridLength.Star),
				new ColumnDefinition(GridLength.Star)
			},
			RowDefinitions =
			{
				new RowDefinition(GridLength.Auto),
				new RowDefinition(GridLength.Auto),
				new RowDefinition(GridLength.Auto)
			},
			ColumnSpacing = 12,
			RowSpacing = 12
		};

		grid.Add(CreateField("Pages/run", _pagesEntry), 0, 0);
		grid.Add(CreateField("Cards/page", _cardsEntry), 1, 0);
		grid.Add(CreateField("Item payload KB/card", _itemPayloadEntry), 0, 1);
		grid.Add(CreateField("Page payload MB/page", _pagePayloadEntry), 1, 1);
		grid.Add(CreateField("Dwell ms/page", _dwellEntry), 0, 2);

		return grid;
	}

	Grid CreateButtonGrid()
	{
		var grid = new Grid
		{
			ColumnDefinitions =
			{
				new ColumnDefinition(GridLength.Star),
				new ColumnDefinition(GridLength.Star)
			},
			RowDefinitions =
			{
				new RowDefinition(GridLength.Auto),
				new RowDefinition(GridLength.Auto)
			},
			ColumnSpacing = 12,
			RowSpacing = 12
		};

		grid.Add(_runLeakButton, 0, 0);
		grid.Add(_runControlButton, 1, 0);
		grid.Add(_runMitigationButton, 0, 1);
		grid.Add(_stopButton, 1, 1);

		return grid;
	}

	static VerticalStackLayout CreateField(string title, Entry entry)
	{
		return new VerticalStackLayout
		{
			Spacing = 4,
			Children =
			{
				new Label
				{
					Text = title,
					FontSize = 12,
					TextColor = Color.FromArgb("#475569")
				},
				entry
			}
		};
	}

	static Entry CreateEntry(string text)
	{
		return new Entry
		{
			Text = text,
			Keyboard = Keyboard.Numeric,
			FontSize = 15,
			TextColor = Color.FromArgb("#1E293B"),
			BackgroundColor = Color.FromArgb("#F1F5F9")
		};
	}

	static Button CreateButton(string text, Func<Task> action)
	{
		var button = new Button
		{
			Text = text,
			FontSize = 14,
			BackgroundColor = Color.FromArgb("#2563EB"),
			TextColor = Colors.White,
			CornerRadius = 6,
			MinimumHeightRequest = 44
		};

		button.Clicked += async (_, _) => await action();
		return button;
	}

	ReproOptions ReadOptions(ReproMode mode)
	{
		return new ReproOptions(
			mode,
			ReadBoundedInt(_pagesEntry.Text, 1, 100, 20),
			ReadBoundedInt(_cardsEntry.Text, 1, 300, 64),
			ReadBoundedInt(_itemPayloadEntry.Text, 0, 512, 96),
			ReadBoundedInt(_pagePayloadEntry.Text, 0, 64, 3),
			ReadBoundedInt(_dwellEntry.Text, 0, 5000, 100));
	}

	static int ReadBoundedInt(string? text, int min, int max, int fallback)
	{
		if (!int.TryParse(text, out var value))
			value = fallback;

		return Math.Min(max, Math.Max(min, value));
	}

	async Task RunAsync(ReproMode mode)
	{
		if (_runCancellation is not null)
			return;

		var options = ReadOptions(mode);
		_runCancellation = new CancellationTokenSource();
		var token = _runCancellation.Token;

		SetRunning(true);
		_progress.Progress = 0;
		_summaryLabel.Text = "Taking baseline after full GC...";

		var session = new ReproSession(options);
		ReproSession.Current = session;

		try
		{
			_baseline = await MemorySampler.TakeAfterCollectionAsync();
			_summaryLabel.Text = $"Baseline captured. Running {options.Name}.";

			for (var i = 0; i < options.Pages; i++)
			{
				token.ThrowIfCancellationRequested();
				var cycle = session.BeginNextCycle();
				_statusLabel.Text = $"Pushing CollectionView page {cycle + 1}/{options.Pages}: {options.Name}";

				await Shell.Current.GoToAsync(AppShell.BorderLeakRoute, animate: false);

				if (options.DwellMilliseconds > 0)
					await Task.Delay(options.DwellMilliseconds, token);

				_progress.Progress = (i + 1d) / (options.Pages * 2d);
			}

			for (var i = 0; i < options.Pages; i++)
			{
				token.ThrowIfCancellationRequested();
				_statusLabel.Text = $"Popping CollectionView page {i + 1}/{options.Pages}: {options.Name}";

				await Shell.Current.GoToAsync("..", animate: false);
				await Task.Delay(25, token);

				if ((i + 1) % 5 == 0 || i + 1 == options.Pages)
				{
					var current = await MemorySampler.TakeAfterCollectionAsync();
					_summaryLabel.Text = session.GetStats(_baseline, current).ToSummary();
				}

				_progress.Progress = (options.Pages + i + 1d) / (options.Pages * 2d);
			}

			var finalSnapshot = await MemorySampler.TakeAfterCollectionAsync();
			_summaryLabel.Text = session.GetStats(_baseline, finalSnapshot).ToSummary();
			_statusLabel.Text = $"Completed {options.Name}.";
		}
		catch (OperationCanceledException)
		{
			_statusLabel.Text = "Run stopped.";
		}
		catch (Exception ex)
		{
			_statusLabel.Text = "Run failed.";
			_summaryLabel.Text = ex.ToString();
		}
		finally
		{
			ReproSession.Current = session;
			_runCancellation?.Dispose();
			_runCancellation = null;
			SetRunning(false);
		}
	}

	Task StopRun()
	{
		_runCancellation?.Cancel();
		return Task.CompletedTask;
	}

	void SetRunning(bool isRunning)
	{
		_runLeakButton.IsEnabled = !isRunning;
		_runControlButton.IsEnabled = !isRunning;
		_runMitigationButton.IsEnabled = !isRunning;
		_stopButton.IsEnabled = isRunning;
	}

	static string? GetMonospaceFontFamily()
	{
#if IOS || MACCATALYST
		return "Menlo";
#else
		return null;
#endif
	}
}
