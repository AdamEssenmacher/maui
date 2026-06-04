namespace IndicatorViewItemsSourceLeakRepro;

public sealed class DashboardPage : ContentPage
{
	readonly Entry _cyclesEntry;
	readonly Entry _feedItemsEntry;
	readonly Entry _payloadEntry;
	readonly Entry _updatesEntry;
	readonly Button _runLeakButton;
	readonly Button _runControlButton;
	readonly Button _runMitigationButton;
	readonly Button _stopButton;
	readonly ProgressBar _progress;
	readonly Label _statusLabel;
	readonly Label _summaryLabel;
	CancellationTokenSource? _runCancellation;
	MemorySnapshot _baseline = MemorySnapshot.Empty;
	bool _autoRunStarted;

	public DashboardPage()
	{
		Title = "IndicatorView ItemsSource Leak";
		BackgroundColor = Colors.White;

		_cyclesEntry = CreateEntry("40");
		_feedItemsEntry = CreateEntry("120");
		_payloadEntry = CreateEntry("1");
		_updatesEntry = CreateEntry("250");

		_runLeakButton = CreateButton("Run shared feed", () => RunAsync(ReproMode.SharedObservableFeed));
		_runControlButton = CreateButton("Run snapshot control", () => RunAsync(ReproMode.SnapshotListControl));
		_runMitigationButton = CreateButton("Run cleanup mitigation", () => RunAsync(ReproMode.ClearIndicatorOnDisappear));
		_stopButton = CreateButton("Stop", StopRun);
		_stopButton.IsEnabled = false;

		_progress = new ProgressBar
		{
			Progress = 0,
			HeightRequest = 6,
			ProgressColor = Color.FromArgb("#146C5A")
		};

		_statusLabel = new Label
		{
			Text = "Ready. Run the snapshot control first, then the shared feed scenario.",
			TextColor = Color.FromArgb("#25312D"),
			FontSize = 14
		};

		_summaryLabel = new Label
		{
			Text = "Each run pushes and pops real Shell pages containing a CarouselView linked to an IndicatorView. The leaky case uses a shared ObservableCollection and tracks control-attached payloads that should die with the old controls.",
			TextColor = Color.FromArgb("#25312D"),
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
						Text = "Shared ObservableCollection retention",
						FontSize = 22,
						FontAttributes = FontAttributes.Bold,
						TextColor = Color.FromArgb("#0B1F33")
					},
					new Label
					{
						Text = "This app exercises the normal CarouselView.IndicatorView API, unwinds the navigation stack, forces full GC, and counts which controls survived.",
						FontSize = 14,
						TextColor = Color.FromArgb("#59665F")
					},
					CreateSettingsGrid(),
					CreateButtonGrid(),
					_progress,
					_statusLabel,
					_summaryLabel
				}
			}
		};

		if (IsAutoRunEnabled())
			Loaded += OnLoadedForAutoRun;
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

		grid.Add(CreateField("Page visits/run", _cyclesEntry), 0, 0);
		grid.Add(CreateField("Shared feed items", _feedItemsEntry), 1, 0);
		grid.Add(CreateField("Control payload MB/visit", _payloadEntry), 0, 1);
		grid.Add(CreateField("Post-GC feed updates", _updatesEntry), 1, 1);

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
					TextColor = Color.FromArgb("#59665F")
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
			TextColor = Color.FromArgb("#25312D"),
			BackgroundColor = Color.FromArgb("#F3F7F4")
		};
	}

	static Button CreateButton(string text, Func<Task> action)
	{
		var button = new Button
		{
			Text = text,
			FontSize = 14,
			BackgroundColor = Color.FromArgb("#146C5A"),
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
			ReadBoundedInt(_cyclesEntry.Text, 1, 200, 40),
			ReadBoundedInt(_feedItemsEntry.Text, 1, 1000, 120),
			ReadBoundedInt(_payloadEntry.Text, 0, 64, 1),
			ReadBoundedInt(_updatesEntry.Text, 0, 5000, 250));
	}

	static int ReadBoundedInt(string? text, int min, int max, int fallback)
	{
		if (!int.TryParse(text, out var value))
			value = fallback;

		return Math.Min(max, Math.Max(min, value));
	}

	async Task RunAsync(ReproMode mode)
	{
		await RunScenarioAsync(mode);
	}

	async Task<string> RunScenarioAsync(ReproMode mode)
	{
		if (_runCancellation is not null)
			return "A run is already in progress.";

		var options = ReadOptions(mode);
		_runCancellation = new CancellationTokenSource();
		var token = _runCancellation.Token;

		SetRunning(true);
		_progress.Progress = 0;
		_summaryLabel.Text = "Taking baseline after full GC...";

		var session = new ReproSession(options);
		ReproSession.Current = session;

		var finalSummary = string.Empty;

		try
		{
			_baseline = await MemorySampler.TakeAfterCollectionAsync();
			session.BaselineFeedUpdateElapsed = session.MeasureFeedUpdateBurst(options.PostGcFeedUpdates);
			_summaryLabel.Text = $"Baseline captured. Running {options.Name}.";

			for (var i = 0; i < options.Cycles; i++)
			{
				token.ThrowIfCancellationRequested();
				var cycle = session.BeginNextCycle();
				_statusLabel.Text = $"Opening feed page {cycle + 1}/{options.Cycles}: {options.Name}";

				await Shell.Current.GoToAsync(AppShell.LeakRoute, animate: false);
				await Task.Delay(GetNavigationSettleDelay(), token);

				_progress.Progress = (i + 0.5d) / options.Cycles;
				_statusLabel.Text = $"Closing feed page {cycle + 1}/{options.Cycles}: {options.Name}";

				await Shell.Current.GoToAsync("..", animate: false);
				await Task.Delay(GetNavigationSettleDelay(), token);

				if ((i + 1) % 5 == 0 || i + 1 == options.Cycles)
				{
					var current = await MemorySampler.TakeAfterCollectionAsync();
					_summaryLabel.Text = session.GetStats(_baseline, current).ToSummary();
				}

				_progress.Progress = (i + 1d) / options.Cycles;
			}

			var beforeUpdates = await MemorySampler.TakeAfterCollectionAsync();
			session.PostGcFeedUpdateElapsed = session.MeasureFeedUpdateBurst(options.PostGcFeedUpdates);
			var finalSnapshot = await MemorySampler.TakeAfterCollectionAsync();
			finalSummary = session.GetStats(_baseline, finalSnapshot, beforeUpdates).ToSummary();
			_summaryLabel.Text = finalSummary;
			_statusLabel.Text = $"Completed {options.Name}.";
		}
		catch (OperationCanceledException)
		{
			_statusLabel.Text = "Run stopped.";
			finalSummary = "Run stopped.";
		}
		catch (Exception ex)
		{
			_statusLabel.Text = "Run failed.";
			finalSummary = ex.ToString();
			_summaryLabel.Text = finalSummary;
		}
		finally
		{
			ReproSession.Current = session;
			_runCancellation?.Dispose();
			_runCancellation = null;
			SetRunning(false);
		}

		return finalSummary;
	}

	async void OnLoadedForAutoRun(object? sender, EventArgs e)
	{
		if (_autoRunStarted)
			return;

		_autoRunStarted = true;
		Loaded -= OnLoadedForAutoRun;

		await Task.Delay(500);

		foreach (var mode in new[]
		{
			ReproMode.SnapshotListControl,
			ReproMode.SharedObservableFeed,
			ReproMode.ClearIndicatorOnDisappear
		})
		{
			var summary = await RunScenarioAsync(mode);
			WriteAutoRunSummary(mode, summary);
			await Task.Delay(500);
		}

		ExitApplication();
	}

	static bool IsAutoRunEnabled()
	{
		var value = Environment.GetEnvironmentVariable("INDICATOR_REPRO_AUTORUN");
		return string.Equals(value, "1", StringComparison.OrdinalIgnoreCase) ||
			string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
	}

	static int GetNavigationSettleDelay()
	{
#if IOS
		return 120;
#else
		return 50;
#endif
	}

	static void WriteAutoRunSummary(ReproMode mode, string summary)
	{
		var text = string.Join(Environment.NewLine,
			$"===== IndicatorViewItemsSourceLeakRepro {mode} BEGIN =====",
			summary,
			$"===== IndicatorViewItemsSourceLeakRepro {mode} END =====");

		Console.WriteLine(text);
		System.Diagnostics.Debug.WriteLine(text);
	}

	static void ExitApplication()
	{
#if ANDROID
		Application.Current?.Quit();
#else
		Environment.Exit(0);
#endif
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
