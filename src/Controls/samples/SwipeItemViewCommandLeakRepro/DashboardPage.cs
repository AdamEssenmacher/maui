using Microsoft.Maui.Controls.Shapes;

namespace SwipeItemViewCommandLeakRepro;

public sealed partial class DashboardPage : ContentPage
{
	readonly Entry _cycleEntry;
	readonly Entry _rowEntry;
	readonly Entry _payloadEntry;
	readonly Entry _dwellEntry;
	readonly Button _leakyButton;
	readonly Button _controlButton;
	readonly Button _mitigationButton;
	readonly Button _stopButton;
	readonly Label _statusLabel;
	readonly Label _summaryLabel;

	CancellationTokenSource? _runCancellation;
	ReproSession? _retainedSession;

	public DashboardPage()
	{
		Title = "SwipeItemView Command Leak";
		BackgroundColor = Color.FromArgb("#F7F8FA");

		_cycleEntry = CreateEntry("25");
		_rowEntry = CreateEntry("40");
		_payloadEntry = CreateEntry("128");
		_dwellEntry = CreateEntry("40");

		_leakyButton = CreateRunButton("Run leaky SwipeItemView", Color.FromArgb("#B42318"));
		_controlButton = CreateRunButton("Run control SwipeItem", Color.FromArgb("#175CD3"));
		_mitigationButton = CreateRunButton("Run mitigation", Color.FromArgb("#067647"));
		_stopButton = CreateRunButton("Stop", Color.FromArgb("#475467"));
		_stopButton.IsEnabled = false;

		_leakyButton.Clicked += async (_, _) => await RunAsync(ReproMode.SwipeItemViewCommand);
		_controlButton.Clicked += async (_, _) => await RunAsync(ReproMode.PlainSwipeItemControl);
		_mitigationButton.Clicked += async (_, _) => await RunAsync(ReproMode.ClearCommandOnDisappear);
		_stopButton.Clicked += (_, _) => _runCancellation?.Cancel();

		_statusLabel = new Label
		{
			Text = "Ready",
			FontSize = 16,
			TextColor = Color.FromArgb("#344054")
		};

		_summaryLabel = new Label
		{
			Text = "Defaults model a dispatch/order app that pushes 25 list pages, each with 40 swipe rows carrying 128 KB of row state. That is 1,000 rows and 125 MB of realistic page-local data.",
			FontSize = 14,
			TextColor = Color.FromArgb("#475467"),
			LineBreakMode = LineBreakMode.WordWrap
		};

		Content = new ScrollView
		{
			Content = new VerticalStackLayout
			{
				Padding = new Thickness(20),
				Spacing = 18,
				Children =
				{
					new Label
					{
						Text = "SwipeItemView.Command retains disposed pages",
						FontSize = 26,
						FontAttributes = FontAttributes.Bold,
						TextColor = Color.FromArgb("#101828")
					},
					new Label
					{
						Text = "The leaky run uses a shared long-lived command, like an app shell or workflow service command. It navigates through pages and then forces collection so retained rows, payloads, swipe views, and command subscribers are visible.",
						FontSize = 15,
						TextColor = Color.FromArgb("#475467"),
						LineBreakMode = LineBreakMode.WordWrap
					},
					CreateSettingsGrid(),
					CreateButtonRow(),
					_statusLabel,
					CreateSummaryFrame()
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

		grid.Add(CreateSetting("Pages per run", _cycleEntry), 0, 0);
		grid.Add(CreateSetting("Swipe rows per page", _rowEntry), 1, 0);
		grid.Add(CreateSetting("Payload KB per row", _payloadEntry), 0, 1);
		grid.Add(CreateSetting("Dwell ms per page", _dwellEntry), 1, 1);

		return grid;
	}

	Grid CreateButtonRow()
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
			ColumnSpacing = 10,
			RowSpacing = 10
		};

		grid.Add(_leakyButton, 0, 0);
		grid.Add(_controlButton, 1, 0);
		grid.Add(_mitigationButton, 0, 1);
		grid.Add(_stopButton, 1, 1);

		return grid;
	}

	Border CreateSummaryFrame()
	{
		return new Border
		{
			Stroke = Color.FromArgb("#D0D5DD"),
			StrokeThickness = 1,
			BackgroundColor = Colors.White,
			StrokeShape = new RoundRectangle
			{
				CornerRadius = 8
			},
			Padding = new Thickness(16),
			Content = _summaryLabel
		};
	}

	static VerticalStackLayout CreateSetting(string label, Entry entry)
	{
		return new VerticalStackLayout
		{
			Spacing = 6,
			Children =
			{
				new Label
				{
					Text = label,
					FontSize = 13,
					TextColor = Color.FromArgb("#475467")
				},
				entry
			}
		};
	}

	static Entry CreateEntry(string value)
	{
		return new Entry
		{
			Text = value,
			Keyboard = Keyboard.Numeric,
			BackgroundColor = Colors.White,
			TextColor = Color.FromArgb("#101828")
		};
	}

	static Button CreateRunButton(string text, Color backgroundColor)
	{
		return new Button
		{
			Text = text,
			BackgroundColor = backgroundColor,
			TextColor = Colors.White,
			FontAttributes = FontAttributes.Bold,
			CornerRadius = 6,
			Padding = new Thickness(14, 10)
		};
	}

	async Task RunAsync(ReproMode mode)
	{
		if (_runCancellation is not null)
			return;

		if (_retainedSession is not null)
			_retainedSession = null;
		var options = CreateOptions(mode);
		using var runCancellation = new CancellationTokenSource();
		_runCancellation = runCancellation;
		var session = new ReproSession(options);
		ReproSession.Current = session;

		SetRunning(true);
		_summaryLabel.Text = $"Starting {options.Name}. Planned payload allocation: {ReproStats.FormatBytes(options.Cycles * options.RowsPerPage * options.PayloadBytesPerRow)}.";

		try
		{
			await RunNavigationLoopAsync(session, runCancellation.Token);
				var stats = await SampleStatsAsync(session, "Final after forced collection");
				LogStats(options, stats, completed: true);
				_summaryLabel.Text = BuildFinalSummary(options, stats);
				_statusLabel.Text = "Complete";
		}
		catch (OperationCanceledException)
		{
			if (ReproSession.Current is not null)
				{
					var stats = await SampleStatsAsync(session, "Canceled after forced collection");
					LogStats(options, stats, completed: false);
					_summaryLabel.Text = stats.ToDisplayString();
				}

			_statusLabel.Text = "Canceled";
		}
		finally
		{
			_retainedSession = session;

			if (ReferenceEquals(ReproSession.Current, session))
				ReproSession.Current = null;

			_runCancellation = null;
			SetRunning(false);
		}
	}

	async Task RunNavigationLoopAsync(ReproSession session, CancellationToken cancellationToken)
	{
		for (var page = 1; page <= session.Options.Cycles; page++)
		{
			cancellationToken.ThrowIfCancellationRequested();
			session.BeginNextCycle();
			_statusLabel.Text = $"Opening page {page:N0} of {session.Options.Cycles:N0}";
			await Shell.Current.GoToAsync(AppShell.SwipeLeakRoute, animate: false);
			await Task.Delay(session.Options.DwellMilliseconds, cancellationToken);
		}

		for (var page = session.Options.Cycles; page >= 1; page--)
		{
			cancellationToken.ThrowIfCancellationRequested();
			_statusLabel.Text = $"Closing page {session.Options.Cycles - page + 1:N0} of {session.Options.Cycles:N0}";
			await Shell.Current.GoToAsync("..", animate: false);
			await Task.Delay(25, cancellationToken);

			if (page % 5 == 0 || page == 1)
			{
				var stats = await SampleStatsAsync(session, $"After closing {session.Options.Cycles - page + 1:N0} pages");
				_summaryLabel.Text = stats.ToDisplayString();
			}
		}
	}

	static async Task<ReproStats> SampleStatsAsync(ReproSession session, string label)
	{
		await Task.Yield();
		MemorySampler.ForceFullCollection();
		session.SharedCommand.RaiseCanExecuteChanged();
		MemorySampler.ForceFullCollection();
		return session.CaptureStats(label);
	}

	ReproOptions CreateOptions(ReproMode mode)
	{
		return new ReproOptions(
			mode,
			ReadPositiveInt(_cycleEntry, 25),
			ReadPositiveInt(_rowEntry, 40),
			ReadPositiveInt(_payloadEntry, 128),
			ReadPositiveInt(_dwellEntry, 40));
	}

	static int ReadPositiveInt(Entry entry, int fallback)
	{
		if (int.TryParse(entry.Text, out var value) && value > 0)
			return value;

		entry.Text = fallback.ToString();
		return fallback;
	}

	static string BuildFinalSummary(ReproOptions options, ReproStats stats)
	{
		var expectedRows = options.Cycles * options.RowsPerPage;
		var expectedPayload = expectedRows * options.PayloadBytesPerRow;

		return
			$"{options.Name}\n\n" +
			stats.ToDisplayString() +
			$"\n\nExpected rows: {expectedRows:N0}\n" +
			$"Expected payload pressure: {ReproStats.FormatBytes(expectedPayload)}\n\n" +
			"The leaky run should retain one command subscriber per SwipeItemView and keep the page-local row payload alive after every page is popped. The control and mitigation runs should fall back near zero after forced collection.";
	}

	static void LogStats(ReproOptions options, ReproStats stats, bool completed)
	{
		var line =
			"SWIPE_REPRO_RESULT " +
			$"completed={completed} " +
			$"mode={options.Mode} " +
			$"cycles={options.Cycles} " +
			$"rowsPerPage={options.RowsPerPage} " +
			$"payloadKBPerRow={options.PayloadKilobytesPerRow} " +
			$"commandSubscribers={stats.CommandSubscribers} " +
			$"alivePages={stats.AlivePages} " +
			$"aliveSwipeViews={stats.AliveSwipeViews} " +
			$"aliveActionElements={stats.AliveActionElements} " +
			$"aliveActionContentViews={stats.AliveActionContentViews} " +
			$"aliveRows={stats.AliveRows} " +
			$"retainedPayloadBytes={stats.RetainedPayloadBytes} " +
			$"heapDeltaBytes={stats.HeapDeltaBytes}";

		Console.WriteLine(line);
		System.Diagnostics.Debug.WriteLine(line);
#if ANDROID
		Android.Util.Log.Info("SwipeRepro", line);
#endif
	}

	void SetRunning(bool running)
	{
		_leakyButton.IsEnabled = !running;
		_controlButton.IsEnabled = !running;
		_mitigationButton.IsEnabled = !running;
		_stopButton.IsEnabled = running;
	}
}
