namespace SwipeItemsLeakRepro;

public sealed class DashboardPage : ContentPage
{
	readonly Entry _cyclesEntry;
	readonly Entry _rowsEntry;
	readonly Entry _payloadEntry;
	readonly Entry _dwellEntry;
	readonly Button _runLeakButton;
	readonly Button _runControlButton;
	readonly Button _runClearButton;
	readonly Button _stopButton;
	readonly ProgressBar _progress;
	readonly Label _statusLabel;
	readonly Label _summaryLabel;
	CancellationTokenSource? _runCancellation;
	MemorySnapshot _baseline = MemorySnapshot.Empty;

	public DashboardPage()
	{
		Title = "SwipeItems Leak Repro";
		BackgroundColor = Colors.White;

		_cyclesEntry = CreateEntry("25");
		_rowsEntry = CreateEntry("40");
		_payloadEntry = CreateEntry("128");
		_dwellEntry = CreateEntry("40");

		_runLeakButton = CreateButton("Run cached SwipeItems", () => RunAsync(ReproMode.CachedSwipeItems));
		_runControlButton = CreateButton("Run control", () => RunAsync(ReproMode.OwnedSwipeItemsControl));
		_runClearButton = CreateButton("Run replace RightItems", () => RunAsync(ReproMode.ReplaceRightItemsOnDisappear));
		_stopButton = CreateButton("Stop", StopRun);
		_stopButton.IsEnabled = false;

		_progress = new ProgressBar
		{
			Progress = 0,
			HeightRequest = 6,
			ProgressColor = Color.FromArgb("#1A7F64")
		};

		_statusLabel = new Label
		{
			Text = "Ready. Run the control first, then cached SwipeItems.",
			TextColor = Color.FromArgb("#24292F"),
			FontSize = 14
		};

		_summaryLabel = new Label
		{
			Text = "Each run pushes a realistic dispatch-board page, pops it, forces full GC, and counts which weak references survived. Defaults allocate about 125 MB of row payload across the run.",
			TextColor = Color.FromArgb("#24292F"),
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
						Text = "SwipeItems retention",
						FontSize = 22,
						FontAttributes = FontAttributes.Bold,
						TextColor = Color.FromArgb("#0B1F33")
					},
					new Label
					{
						Text = "This app models a field-service list with cached row action menus. The cached SwipeItems root old SwipeViews through CollectionChanged/PropertyChanged delegates.",
						FontSize = 14,
						TextColor = Color.FromArgb("#57606A")
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
				new RowDefinition(GridLength.Auto)
			},
			ColumnSpacing = 12,
			RowSpacing = 12
		};

		grid.Add(CreateField("Pages/run", _cyclesEntry), 0, 0);
		grid.Add(CreateField("Swipe rows/page", _rowsEntry), 1, 0);
		grid.Add(CreateField("Payload KB/row", _payloadEntry), 0, 1);
		grid.Add(CreateField("Dwell ms/page", _dwellEntry), 1, 1);

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
		grid.Add(_runClearButton, 0, 1);
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
					TextColor = Color.FromArgb("#57606A")
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
			TextColor = Color.FromArgb("#24292F"),
			BackgroundColor = Color.FromArgb("#F6F8FA")
		};
	}

	static Button CreateButton(string text, Func<Task> action)
	{
		var button = new Button
		{
			Text = text,
			FontSize = 14,
			BackgroundColor = Color.FromArgb("#1A7F64"),
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
			ReadBoundedInt(_cyclesEntry.Text, 1, 100, 25),
			ReadBoundedInt(_rowsEntry.Text, 1, 250, 40),
			ReadBoundedInt(_payloadEntry.Text, 0, 2048, 128),
			ReadBoundedInt(_dwellEntry.Text, 0, 5000, 40));
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
		_summaryLabel.Text = "Clearing previous cached SwipeItems and taking baseline after full GC...";
		SharedSwipeActionCache.Reset();
		ReproSession.Current = null;

		var session = new ReproSession(options);

		try
		{
			_baseline = await MemorySampler.TakeAfterCollectionAsync();
			ReproSession.Current = session;
			_summaryLabel.Text = $"Baseline captured. Running {options.Name}.";

			for (var i = 0; i < options.Cycles; i++)
			{
				token.ThrowIfCancellationRequested();
				var cycle = session.BeginNextCycle();
				_statusLabel.Text = $"Pushing dispatch page {cycle + 1}/{options.Cycles}: {options.Name}";

				await Shell.Current.GoToAsync(AppShell.SwipeLeakRoute, animate: false);

				if (options.DwellMilliseconds > 0)
					await Task.Delay(options.DwellMilliseconds, token);

				_statusLabel.Text = $"Popping dispatch page {cycle + 1}/{options.Cycles}: {options.Name}";
				await Shell.Current.GoToAsync("..", animate: false);
				await Task.Delay(25, token);

				if ((i + 1) % 5 == 0 || i + 1 == options.Cycles)
				{
					var current = await MemorySampler.TakeAfterCollectionAsync();
					var stats = session.GetStats(_baseline, current);
					_summaryLabel.Text = stats.ToSummary();
				}

				_progress.Progress = (i + 1d) / options.Cycles;
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
		_runClearButton.IsEnabled = !isRunning;
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
