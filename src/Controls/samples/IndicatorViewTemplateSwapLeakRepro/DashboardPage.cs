using Microsoft.Maui.Controls.Shapes;
using IOPath = System.IO.Path;

namespace IndicatorViewTemplateSwapLeakRepro;

public sealed class DashboardPage : ContentPage
{
	readonly Entry _templateStatesEntry;
	readonly Entry _indicatorItemsEntry;
	readonly Entry _payloadEntry;
	readonly Entry _positionUpdatesEntry;
	readonly Button _runLeakButton;
	readonly Button _runControlButton;
	readonly Button _runMitigationButton;
	readonly Button _stopButton;
	readonly ContentView _hostViewport;
	readonly ProgressBar _progress;
	readonly Label _statusLabel;
	readonly Label _summaryLabel;
	CancellationTokenSource? _runCancellation;
	IndicatorTemplateHost? _currentHost;
	string _lastSummary = string.Empty;
	bool _autoRunStarted;

	public DashboardPage()
	{
		Title = "IndicatorView Template Swap Leak";
		BackgroundColor = Colors.White;

		_templateStatesEntry = CreateEntry("40");
		_indicatorItemsEntry = CreateEntry("8");
		_payloadEntry = CreateEntry("192");
		_positionUpdatesEntry = CreateEntry("1000");

		_runLeakButton = CreateButton("Run leak", () => RunAsync(ReproMode.DirectTemplateReplace));
		_runControlButton = CreateButton("Run control", () => RunAsync(ReproMode.StaticTemplateControl));
		_runMitigationButton = CreateButton("Run mitigation", () => RunAsync(ReproMode.ClearThenReplaceMitigation));
		_stopButton = CreateButton("Stop", StopRun);
		_stopButton.IsEnabled = false;

		_hostViewport = new ContentView
		{
			HeightRequest = 470,
			BackgroundColor = Color.FromArgb("#F6F8FA")
		};

		_progress = new ProgressBar
		{
			Progress = 0,
			HeightRequest = 6,
			ProgressColor = Color.FromArgb("#0F6B5B")
		};

		_statusLabel = new Label
		{
			Text = "Ready. Run the control first, then the direct non-null replacement scenario.",
			TextColor = Color.FromArgb("#172026"),
			FontSize = 14
		};

		_summaryLabel = new Label
		{
			Text = "This page keeps one IndicatorView alive, alternates custom IndicatorTemplate values, forces full GC, and counts retired layouts that stayed alive.",
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
						Text = "IndicatorTemplate replacement retention",
						FontSize = 22,
						FontAttributes = FontAttributes.Bold,
						TextColor = Color.FromArgb("#0B1F33")
					},
					new Label
					{
						Text = "This uses a real CarouselView + IndicatorView pair and swaps between two realistic indicator templates on the same live control.",
						FontSize = 14,
						TextColor = Color.FromArgb("#57606A")
					},
					CreateSettingsGrid(),
					CreateButtonGrid(),
					CreateHostSurface(),
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

		grid.Add(CreateField("Template states/run", _templateStatesEntry), 0, 0);
		grid.Add(CreateField("Indicator items", _indicatorItemsEntry), 1, 0);
		grid.Add(CreateField("Payload KB/indicator", _payloadEntry), 0, 1);
		grid.Add(CreateField("Post-GC position updates", _positionUpdatesEntry), 1, 1);

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
					TextColor = Color.FromArgb("#57606A")
				},
				entry
			}
		};
	}

	View CreateHostSurface()
	{
		return new Border
		{
			StrokeThickness = 1,
			Stroke = Color.FromArgb("#D0D7DE"),
			BackgroundColor = Colors.White,
			StrokeShape = new RoundRectangle { CornerRadius = 8 },
			Padding = new Thickness(12),
			Content = new VerticalStackLayout
			{
				Spacing = 10,
				Children =
				{
					new Label
					{
						Text = "Live host under test",
						FontSize = 14,
						FontAttributes = FontAttributes.Bold,
						TextColor = Color.FromArgb("#172026")
					},
					new Label
					{
						Text = "The current run rebuilds this host fresh, keeps it alive for the full scenario, and tracks only retired indicator layouts.",
						FontSize = 13,
						TextColor = Color.FromArgb("#57606A")
					},
					_hostViewport
				}
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
			BackgroundColor = Color.FromArgb("#0F6B5B"),
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
			ReadBoundedInt(_templateStatesEntry.Text, 1, 200, 40),
			ReadBoundedInt(_indicatorItemsEntry.Text, 1, 24, 8),
			ReadBoundedInt(_payloadEntry.Text, 0, 2048, 192),
			ReadBoundedInt(_positionUpdatesEntry.Text, 0, 10000, 1000));
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
		var tokenSource = new CancellationTokenSource();
		_runCancellation = tokenSource;

		SetRunning(true);
		_progress.Progress = 0;
		_statusLabel.Text = $"Preparing {options.Name}.";
		_summaryLabel.Text = "Clearing the previous host and forcing a baseline collection...";

		try
		{
			await ResetHostAsync();

			var session = new ReproSession(options);
			var host = new IndicatorTemplateHost(session);
			_currentHost = host;
			_hostViewport.Content = host;

			await host.InitializeAsync(tokenSource.Token);
			var baseline = await MemorySampler.TakeAfterCollectionAsync();
			var baselineBurst = await host.MeasurePositionUpdateBurstAsync(options.PostGcPositionUpdates, tokenSource.Token);

			_summaryLabel.Text = "Initial template materialized. Running the scenario...";
			await RunScenarioStepsAsync(options, host, tokenSource.Token);

			_statusLabel.Text = "Collecting after the run and measuring post-run Position updates...";
			await MemorySampler.TakeAfterCollectionAsync();
			var postRunBurst = await host.MeasurePositionUpdateBurstAsync(options.PostGcPositionUpdates, tokenSource.Token);
			var finalSnapshot = await MemorySampler.TakeAfterCollectionAsync();

			var stats = session.GetStats(baseline, finalSnapshot, baselineBurst, postRunBurst);
			_lastSummary = stats.ToSummary();
			_summaryLabel.Text = _lastSummary;
			_statusLabel.Text = $"Completed {options.Name}.";
			_progress.Progress = 1;
		}
		catch (OperationCanceledException)
		{
			_statusLabel.Text = "Run stopped.";
			_lastSummary = _summaryLabel.Text;
		}
		catch (Exception ex)
		{
			_statusLabel.Text = "Run failed.";
			_lastSummary = ex.ToString();
			_summaryLabel.Text = _lastSummary;
		}
		finally
		{
			tokenSource.Dispose();
			_runCancellation = null;
			SetRunning(false);
		}
	}

	async Task RunScenarioStepsAsync(ReproOptions options, IndicatorTemplateHost host, CancellationToken token)
	{
		if (options.Mode == ReproMode.StaticTemplateControl || options.TemplateStateCount <= 1)
		{
			_progress.Progress = 1;
			_statusLabel.Text = "Control scenario: the initial non-null template stays active.";
			return;
		}

		for (var generationIndex = 1; generationIndex < options.TemplateStateCount; generationIndex++)
		{
			token.ThrowIfCancellationRequested();

			var clearFirst = options.Mode == ReproMode.ClearThenReplaceMitigation;
			_statusLabel.Text = clearFirst
				? $"Mitigation replacement {generationIndex}/{options.TemplateStateCount - 1}: clear, settle, then assign the next non-null template."
				: $"Leak replacement {generationIndex}/{options.TemplateStateCount - 1}: direct non-null template replacement.";

			await host.ApplyNextTemplateAsync(generationIndex, clearFirst, token);
			_progress.Progress = generationIndex / (double)(options.TemplateStateCount - 1);
		}
	}

	async Task RunAllScenariosAsync()
	{
		try
		{
			var report = new List<string>
			{
				$"IndicatorViewTemplateSwapLeakRepro autorun started at {DateTimeOffset.Now:O}",
				$"Defaults: templateStates={_templateStatesEntry.Text}, indicatorItems={_indicatorItemsEntry.Text}, payloadKB={_payloadEntry.Text}, positionUpdates={_positionUpdatesEntry.Text}"
			};

			foreach (var mode in new[]
			{
				ReproMode.StaticTemplateControl,
				ReproMode.DirectTemplateReplace,
				ReproMode.ClearThenReplaceMitigation
			})
			{
				await RunAsync(mode);
				report.Add(string.Empty);
				report.Add(_lastSummary);
				await Task.Delay(250);
			}

			var reportText = string.Join(Environment.NewLine, report);
			Console.WriteLine(reportText);

			var writeResult = await WriteAutoRunReportAsync(reportText);
			if (writeResult.ExplicitPathFailure is not null)
			{
				Console.WriteLine($"Requested autorun results path failed: {writeResult.RequestedPath}");
				Console.WriteLine(writeResult.ExplicitPathFailure);
				Console.WriteLine($"Fell back to: {writeResult.ActualPath}");
			}
			else
			{
				Console.WriteLine($"Autorun results written to: {writeResult.ActualPath}");
			}

			await Task.Delay(250);
			Environment.Exit(0);
		}
		catch (Exception ex)
		{
			_lastSummary = ex.ToString();
			_statusLabel.Text = "Autorun failed.";
			_summaryLabel.Text = _lastSummary;
			Console.Error.WriteLine(ex);
			await Task.Delay(250);
			Environment.Exit(1);
		}
	}

	async Task ResetHostAsync()
	{
		_currentHost = null;
		_hostViewport.Content = null;
		await MemorySampler.ForceFullCollectionAsync();
	}

	static async Task<AutoRunReportWriteResult> WriteAutoRunReportAsync(string reportText)
	{
		var requestedPath = string.IsNullOrWhiteSpace(AutoRunSettings.ResultsPath)
			? null
			: AutoRunSettings.ResultsPath;
		var paths = new List<string>();

		if (requestedPath is not null)
			paths.Add(requestedPath);

		paths.Add(IOPath.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "IndicatorViewTemplateSwapLeakRepro", "autorun-results.txt"));
		paths.Add(IOPath.Combine(IOPath.GetTempPath(), "indicatorviewtemplateswapleakrepro-results.txt"));

		string? explicitPathFailure = null;
		Exception? lastException = null;

		foreach (var path in paths.Distinct())
		{
			try
			{
				var directory = IOPath.GetDirectoryName(path);
				if (!string.IsNullOrWhiteSpace(directory))
					Directory.CreateDirectory(directory);

				await File.WriteAllTextAsync(path, reportText);
				return new AutoRunReportWriteResult(requestedPath, path, explicitPathFailure);
			}
			catch (Exception ex)
			{
				if (requestedPath is not null && string.Equals(path, requestedPath, StringComparison.Ordinal))
					explicitPathFailure = $"{ex.GetType().Name}: {ex.Message}";

				lastException = ex;
			}
		}

		var attemptedPaths = string.Join(", ", paths.Distinct());
		throw new IOException($"Unable to write autorun results to any candidate path. Attempted: {attemptedPaths}", lastException);
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

readonly record struct AutoRunReportWriteResult(
	string? RequestedPath,
	string ActualPath,
	string? ExplicitPathFailure);
