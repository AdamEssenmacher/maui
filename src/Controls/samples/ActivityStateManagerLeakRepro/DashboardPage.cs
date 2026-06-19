using System.Diagnostics;
using ActivityStateChangedEventArgs = Microsoft.Maui.ApplicationModel.ActivityStateChangedEventArgs;
using MauiPlatform = Microsoft.Maui.ApplicationModel.Platform;

namespace ActivityStateManagerLeakRepro;

public sealed class DashboardPage : ContentPage
{
	readonly Entry _recreatesEntry;
	readonly Entry _subscribersEntry;
	readonly Entry _workEntry;
	readonly Entry _delayEntry;
	readonly Button _runButton;
	readonly Button _stopButton;
	readonly ProgressBar _progress;
	readonly Label _statusLabel;
	readonly Label _summaryLabel;
	CancellationTokenSource? _runCancellation;
	static bool s_autoRunStarted;
	string _lastSummary = string.Empty;

	public DashboardPage()
	{
		Title = "ActivityStateManager Leak Repro";
		BackgroundColor = Colors.White;

		_recreatesEntry = CreateEntry(LeakRunOptions.DefaultRecreateCount.ToString());
		_subscribersEntry = CreateEntry(LeakRunOptions.DefaultSubscriberCount.ToString());
		_workEntry = CreateEntry(LeakRunOptions.DefaultEstimatedWorkMillisecondsPerSubscriber.ToString());
		_delayEntry = CreateEntry(LeakRunOptions.DefaultDelayMilliseconds.ToString());

		_runButton = CreateButton("Run leak repro", RunAsync);
		_stopButton = CreateButton("Stop", StopRun);
		_stopButton.IsEnabled = false;

		_progress = new ProgressBar
		{
			Progress = 0,
			HeightRequest = 6,
			ProgressColor = Color.FromArgb("#115E67")
		};

		_statusLabel = new Label
		{
			Text = "Ready. The default run models a long-lived field app over a work shift.",
			TextColor = Color.FromArgb("#172026"),
			FontSize = 14
		};

		_summaryLabel = new Label
		{
			Text = "The repro recreates the Android Activity, forces full GC, then reports retained ActivityStateManager lifecycle callbacks and duplicated Platform.ActivityStateChanged notifications.",
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
						Text = "ActivityStateManager callback retention",
						FontSize = 22,
						FontAttributes = FontAttributes.Bold,
						TextColor = Color.FromArgb("#0B1F33")
					},
					new Label
					{
						Text = "A single Activity recreation should not permanently add another Android lifecycle callback or multiply app lifecycle subscribers.",
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

		if (AutoRunSettings.Enabled && !s_autoRunStarted)
		{
			s_autoRunStarted = true;
			Dispatcher.Dispatch(async () => await RunAutoAsync());
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

		grid.Add(CreateField("Activity recreations", _recreatesEntry), 0, 0);
		grid.Add(CreateField("Lifecycle subscribers", _subscribersEntry), 1, 0);
		grid.Add(CreateField("Estimated work ms", _workEntry), 0, 1);
		grid.Add(CreateField("Delay ms/recreate", _delayEntry), 1, 1);

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

		grid.Add(_runButton, 0);
		grid.Add(_stopButton, 1);

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
			BackgroundColor = Color.FromArgb("#115E67"),
			TextColor = Colors.White,
			CornerRadius = 6,
			MinimumHeightRequest = 44
		};

		button.Clicked += async (_, _) => await action();
		return button;
	}

	LeakRunOptions ReadOptions()
	{
		return new LeakRunOptions(
			ReadBoundedInt(_recreatesEntry.Text, 1, 500, LeakRunOptions.DefaultRecreateCount),
			ReadBoundedInt(_subscribersEntry.Text, 1, 16, LeakRunOptions.DefaultSubscriberCount),
			ReadBoundedInt(_workEntry.Text, 0, 2000, LeakRunOptions.DefaultEstimatedWorkMillisecondsPerSubscriber),
			ReadBoundedInt(_delayEntry.Text, 0, 5000, LeakRunOptions.DefaultDelayMilliseconds));
	}

	static int ReadBoundedInt(string? text, int min, int max, int fallback)
	{
		if (!int.TryParse(text, out var value))
			value = fallback;

		return Math.Min(max, Math.Max(min, value));
	}

	async Task RunAsync()
	{
		if (_runCancellation is not null)
			return;

		var options = ReadOptions();
		var handlers = new List<EventHandler<ActivityStateChangedEventArgs>>();
		var subscriberInvocations = 0L;
		var elapsed = Stopwatch.StartNew();

		_runCancellation = new CancellationTokenSource();
		var token = _runCancellation.Token;

		SetRunning(true);
		_progress.Progress = 0;
		_statusLabel.Text = "Taking baseline after full GC...";
		_summaryLabel.Text = "Baseline collection in progress.";

		try
		{
			var baseline = await ReproMetrics.TakeSnapshotAfterCollectionAsync();

			for (var i = 0; i < options.SubscriberCount; i++)
			{
				EventHandler<ActivityStateChangedEventArgs> handler = (_, _) =>
					Interlocked.Increment(ref subscriberInvocations);

				handlers.Add(handler);
				MauiPlatform.ActivityStateChanged += handler;
			}

			_statusLabel.Text = $"Recreating Activity 0/{options.RecreateCount}.";
			_summaryLabel.Text = $"Baseline ActivityStateManager listener registrations: {baseline.ActivityStateManagerRegistrations}.";

			var progress = new Progress<RecreateProgress>(value =>
			{
				_progress.Progress = value.CompletedRecreates / (double)value.TotalRecreates;
				_statusLabel.Text = $"Recreating Activity {value.CompletedRecreates}/{value.TotalRecreates}. Current Activity instance #{value.CurrentActivityInstanceId}.";
			});

			await ActivityRecreationDriver.RunAsync(options, progress, token);

			foreach (var handler in handlers)
				MauiPlatform.ActivityStateChanged -= handler;

			handlers.Clear();

			elapsed.Stop();

			_statusLabel.Text = "Run complete. Forcing full GC and counting retained callbacks...";
			var final = await ReproMetrics.TakeSnapshotAfterCollectionAsync();
			var report = new ReproReport(options, baseline, final, subscriberInvocations, elapsed.Elapsed);
			_lastSummary = report.ToSummary();
			_summaryLabel.Text = _lastSummary;
			_statusLabel.Text = "Completed leak repro.";
		}
		catch (OperationCanceledException)
		{
			elapsed.Stop();
			_statusLabel.Text = "Run stopped.";
			_lastSummary = _summaryLabel.Text;
		}
		catch (Exception ex)
		{
			elapsed.Stop();
			_statusLabel.Text = "Run failed.";
			_lastSummary = ex.ToString();
			_summaryLabel.Text = _lastSummary;
		}
		finally
		{
			foreach (var handler in handlers)
				MauiPlatform.ActivityStateChanged -= handler;

			_runCancellation?.Dispose();
			_runCancellation = null;
			SetRunning(false);
		}
	}

	async Task RunAutoAsync()
	{
		await Task.Delay(500);
		await RunAsync();

		var reportText = string.Join(Environment.NewLine,
			$"ActivityStateManagerLeakRepro autorun started at {DateTimeOffset.Now:O}",
			$"Defaults: recreates={_recreatesEntry.Text}, subscribers={_subscribersEntry.Text}, workMs={_workEntry.Text}, delayMs={_delayEntry.Text}",
			string.Empty,
			_lastSummary);

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

		paths.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ActivityStateManagerLeakRepro", "autorun-results.txt"));
		paths.Add(Path.Combine(Path.GetTempPath(), "activitystatemanagerleakrepro-results.txt"));

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
		_runButton.IsEnabled = !isRunning;
		_stopButton.IsEnabled = isRunning;
	}

	static string? GetMonospaceFontFamily() => null;
}
