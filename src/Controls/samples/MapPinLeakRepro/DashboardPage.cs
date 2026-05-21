namespace MapPinLeakRepro;

public sealed class DashboardPage : ContentPage
{
	readonly Entry _cyclesEntry;
	readonly Entry _pinsEntry;
	readonly Entry _dwellEntry;
	readonly Button _runControlButton;
	readonly Button _runLeakButton;
	readonly Button _stopButton;
	readonly ProgressBar _progress;
	readonly Label _statusLabel;
	readonly Label _summaryLabel;
	CancellationTokenSource? _runCancellation;
	MemorySnapshot _baseline = MemorySnapshot.Empty;
	bool _autoRunStarted;
	string _lastSummary = string.Empty;

	public DashboardPage()
	{
		Title = "Map Pin Leak Repro";
		BackgroundColor = Colors.White;

		_cyclesEntry = CreateEntry("20");
		_pinsEntry = CreateEntry("8");
		_dwellEntry = CreateEntry("100");

		_runControlButton = CreateButton("Run control", () => RunAsync(ReproMode.CurrentPinsControl));
		_runLeakButton = CreateButton("Run removed-pin leak", () => RunAsync(ReproMode.RemovedPinsLeak));
		_stopButton = CreateButton("Stop", StopRun);
		_stopButton.IsEnabled = false;

		_progress = new ProgressBar
		{
			Progress = 0,
			HeightRequest = 6,
			ProgressColor = Color.FromArgb("#194D7A")
		};

		_statusLabel = new Label
		{
			Text = "Ready. Run the control first, then the removed-pin leak scenario.",
			TextColor = Color.FromArgb("#172026"),
			FontSize = 14
		};

		_summaryLabel = new Label
		{
			Text = "Each run pushes real Shell pages containing real Android Maps, mutates Pins, unwinds the stack, forces full GC, and counts which weak references survived.",
			TextColor = Color.FromArgb("#172026"),
			FontFamily = null,
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
						Text = "Android MapHandler pin retention",
						FontSize = 22,
						FontAttributes = FontAttributes.Bold,
						TextColor = Color.FromArgb("#0B1F33")
					},
					new Label
					{
						Text = "This holds Pin objects in a long-lived cache. Retained current pins are unsubscribed when the map page is disposed; retained removed pins expose stale Pin.PropertyChanged subscriptions.",
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

	protected override void OnAppearing()
	{
		base.OnAppearing();

		if (AutoRunSettings.Enabled && !_autoRunStarted)
		{
			_autoRunStarted = true;
			Dispatcher.Dispatch(async () => await RunAllScenariosAsync());
		}
	}

	Grid CreateSettingsGrid()
	{
		var grid = new Grid
		{
			ColumnDefinitions =
			{
				new ColumnDefinition(GridLength.Star),
				new ColumnDefinition(GridLength.Star),
				new ColumnDefinition(GridLength.Star)
			},
			ColumnSpacing = 12
		};

		grid.Add(CreateField("Pages/run", _cyclesEntry), 0, 0);
		grid.Add(CreateField("Pins/page", _pinsEntry), 1, 0);
		grid.Add(CreateField("Dwell ms/page", _dwellEntry), 2, 0);

		return grid;
	}

	Grid CreateButtonGrid()
	{
		var grid = new Grid
		{
			ColumnDefinitions =
			{
				new ColumnDefinition(GridLength.Star),
				new ColumnDefinition(GridLength.Star),
				new ColumnDefinition(GridLength.Star)
			},
			ColumnSpacing = 12
		};

		grid.Add(_runControlButton, 0, 0);
		grid.Add(_runLeakButton, 1, 0);
		grid.Add(_stopButton, 2, 0);

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
			TextColor = Color.FromArgb("#172026"),
			BackgroundColor = Color.FromArgb("#F6F8FA")
		};
	}

	static Button CreateButton(string text, Func<Task> action)
	{
		var button = new Button
		{
			Text = text,
			FontSize = 13,
			BackgroundColor = Color.FromArgb("#194D7A"),
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
			ReadBoundedInt(_cyclesEntry.Text, 1, 100, 20),
			ReadBoundedInt(_pinsEntry.Text, 1, 100, 8),
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

			for (var i = 0; i < options.Cycles; i++)
			{
				token.ThrowIfCancellationRequested();
				var cycle = session.BeginNextCycle();
				_statusLabel.Text = $"Pushing map page {cycle + 1}/{options.Cycles}: {options.Name}";

				await Shell.Current.GoToAsync(AppShell.MapPinLeakRoute, animate: false);
				await session.WaitForCurrentPageReadyAsync(token);

				if (options.DwellMilliseconds > 0)
					await Task.Delay(options.DwellMilliseconds, token);

				_progress.Progress = (i + 1d) / (options.Cycles * 2d);
			}

			for (var i = 0; i < options.Cycles; i++)
			{
				token.ThrowIfCancellationRequested();
				_statusLabel.Text = $"Popping map page {i + 1}/{options.Cycles}: {options.Name}";

				await Shell.Current.GoToAsync("..", animate: false);
				await Task.Delay(50, token);

				if ((i + 1) % 5 == 0 || i + 1 == options.Cycles)
				{
					var current = await MemorySampler.TakeAfterCollectionAsync();
					var stats = session.GetStats(_baseline, current);
					_summaryLabel.Text = stats.ToSummary();
				}

				_progress.Progress = (options.Cycles + i + 1d) / (options.Cycles * 2d);
			}

			var finalSnapshot = await MemorySampler.TakeAfterCollectionAsync();
			_summaryLabel.Text = session.GetStats(_baseline, finalSnapshot).ToSummary();
			_lastSummary = _summaryLabel.Text;
			_statusLabel.Text = $"Completed {options.Name}.";
		}
		catch (OperationCanceledException)
		{
			_statusLabel.Text = "Run stopped.";
			_lastSummary = _summaryLabel.Text;
		}
		catch (Exception ex)
		{
			_statusLabel.Text = "Run failed.";
			_summaryLabel.Text = ex.ToString();
			_lastSummary = _summaryLabel.Text;
		}
		finally
		{
			ReproSession.Current = session;
			_runCancellation?.Dispose();
			_runCancellation = null;
			SetRunning(false);
		}
	}

	async Task RunAllScenariosAsync()
	{
		var report = new List<string>
		{
			$"MapPinLeakRepro autorun started at {DateTimeOffset.Now:O}",
			$"Defaults: pages={_cyclesEntry.Text}, pins={_pinsEntry.Text}, dwellMs={_dwellEntry.Text}"
		};

		foreach (var mode in new[]
		{
			ReproMode.CurrentPinsControl,
			ReproMode.RemovedPinsLeak
		})
		{
			await RunAsync(mode);
			report.Add(string.Empty);
			report.Add(_lastSummary);
			await Task.Delay(250);
		}

		var reportText = string.Join(Environment.NewLine, report);
		Console.WriteLine(reportText);
		await WriteAutoRunReportAsync(reportText);
		await Task.Delay(250);
		Environment.Exit(0);
	}

	static async Task WriteAutoRunReportAsync(string reportText)
	{
		var paths = new List<string>();

		if (!string.IsNullOrWhiteSpace(AutoRunSettings.ResultsPath))
			paths.Add(AutoRunSettings.ResultsPath);

		paths.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MapPinLeakRepro", "autorun-results.txt"));
		paths.Add(Path.Combine(Path.GetTempPath(), "mappinleakrepro-results.txt"));

		Exception? lastException = null;

		foreach (var path in paths.Distinct())
		{
			try
			{
				var directory = Path.GetDirectoryName(path);
				if (!string.IsNullOrWhiteSpace(directory))
					Directory.CreateDirectory(directory);

				await File.WriteAllTextAsync(path, reportText);
				return;
			}
			catch (Exception ex)
			{
				lastException = ex;
			}
		}

		if (lastException is not null)
			Console.WriteLine(lastException);
	}

	Task StopRun()
	{
		_runCancellation?.Cancel();
		return Task.CompletedTask;
	}

	void SetRunning(bool isRunning)
	{
		_runControlButton.IsEnabled = !isRunning;
		_runLeakButton.IsEnabled = !isRunning;
		_stopButton.IsEnabled = isRunning;
	}
}
