namespace AppActionsOnAppActionLeakRepro;

public sealed class DashboardPage : ContentPage
{
	readonly Entry _cyclesEntry;
	readonly Entry _payloadEntry;
	readonly Entry _dwellEntry;
	readonly Button _runLeakButton;
	readonly Button _runControlButton;
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
		Title = "AppActions OnAppAction Leak Repro";
		BackgroundColor = Colors.White;

		_cyclesEntry = CreateEntry("60");
		_payloadEntry = CreateEntry("2");
		_dwellEntry = CreateEntry("5");

		_runLeakButton = CreateButton("Run ConfigureEssentials leak", () => RunAsync(ReproMode.ConfigureEssentialsOnAppAction));
		_runControlButton = CreateButton("Run control", () => RunAsync(ReproMode.NoAppActionControl));
		_stopButton = CreateButton("Stop", StopRun);
		_stopButton.IsEnabled = false;

		_progress = new ProgressBar
		{
			Progress = 0,
			HeightRequest = 6,
			ProgressColor = Color.FromArgb("#6D4C41")
		};

		_statusLabel = new Label
		{
			Text = "Ready. Run the control first, then the ConfigureEssentials app-action scenario.",
			TextColor = Color.FromArgb("#172026"),
			FontSize = 14
		};

		_summaryLabel = new Label
		{
			Text = "Each run builds and disposes throwaway MauiApp instances, forces full GC, and counts which app-action payloads survived.",
			TextColor = Color.FromArgb("#172026"),
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
						Text = "AppActions OnAppAction initializer retention",
						FontSize = 22,
						FontAttributes = FontAttributes.Bold,
						TextColor = Color.FromArgb("#0B1F33")
					},
					new Label
					{
						Text = "This models repeated MAUI host construction in tests, embedding, previews, or plugin hosts that configure app-action handlers.",
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

		grid.Add(CreateField("MauiApps/run", _cyclesEntry), 0, 0);
		grid.Add(CreateField("Payload MB/app", _payloadEntry), 1, 0);
		grid.Add(CreateField("Dwell ms/app", _dwellEntry), 0, 1);

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
			ColumnSpacing = 12
		};

		grid.Add(_runLeakButton, 0, 0);
		grid.Add(_runControlButton, 1, 0);

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
			FontSize = 14,
			BackgroundColor = Color.FromArgb("#6D4C41"),
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
			ReadBoundedInt(_cyclesEntry.Text, 1, 200, 60),
			ReadBoundedInt(_payloadEntry.Text, 0, 64, 2),
			ReadBoundedInt(_dwellEntry.Text, 0, 5000, 5));
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

		try
		{
			_baseline = await MemorySampler.TakeAfterCollectionAsync();
			_summaryLabel.Text = $"Baseline captured. Running {options.Name}.";

			for (var i = 0; i < options.Cycles; i++)
			{
				token.ThrowIfCancellationRequested();
				_statusLabel.Text = $"Building throwaway MauiApp {i + 1}/{options.Cycles}: {options.Name}";

				session.RunCycle(i);

				if (options.DwellMilliseconds > 0)
					await Task.Delay(options.DwellMilliseconds, token);

				if ((i + 1) % 10 == 0 || i + 1 == options.Cycles)
				{
					var current = await MemorySampler.TakeAfterCollectionAsync();
					_summaryLabel.Text = session.GetStats(_baseline, current).ToSummary();
				}

				_progress.Progress = (i + 1d) / options.Cycles;
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
			_runCancellation?.Dispose();
			_runCancellation = null;
			SetRunning(false);
		}
	}

	async Task RunAllScenariosAsync()
	{
		var report = new List<string>
		{
			$"AppActionsOnAppActionLeakRepro autorun started at {DateTimeOffset.Now:O}",
			$"Defaults: apps={_cyclesEntry.Text}, payloadMB={_payloadEntry.Text}, dwellMs={_dwellEntry.Text}"
		};

		foreach (var mode in new[]
		{
			ReproMode.NoAppActionControl,
			ReproMode.ConfigureEssentialsOnAppAction
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

		paths.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AppActionsOnAppActionLeakRepro", "autorun-results.txt"));
		paths.Add(Path.Combine(Path.GetTempPath(), "appactionsonappactionleakrepro-results.txt"));

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
		_runLeakButton.IsEnabled = !isRunning;
		_runControlButton.IsEnabled = !isRunning;
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
