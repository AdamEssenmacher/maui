using System.Globalization;

namespace SelectedItemsLeakRepro;

public sealed class DashboardPage : ContentPage
{
#if AUTORUN_METRICS
	static bool s_autoRunStarted;
#endif

	readonly Entry _cyclesEntry;
	readonly Entry _rowsEntry;
	readonly Entry _selectedEntry;
	readonly Entry _payloadEntry;
	readonly Entry _dwellEntry;
	readonly Button _runLeakyButton;
	readonly Button _runRetainedListButton;
	readonly Button _runPageScopedButton;
	readonly Button _clearButton;
	readonly Button _stopButton;
	readonly Label _statusLabel;
	readonly Label _summaryLabel;
	readonly ProgressBar _progress;

	CancellationTokenSource? _runCancellation;
	MemorySnapshot _baseline = MemorySnapshot.Empty;

	public DashboardPage()
	{
		Title = "SelectedItems Leak";

		_cyclesEntry = CreateNumberEntry("25");
		_rowsEntry = CreateNumberEntry("600");
		_selectedEntry = CreateNumberEntry("40");
		_payloadEntry = CreateNumberEntry("4");
		_dwellEntry = CreateNumberEntry("60");

		_runLeakyButton = CreateButton("Run leaky ObservableCollection", () => RunAsync(ReproMode.ObservableSelection));
		_runRetainedListButton = CreateButton("Run retained List control", () => RunAsync(ReproMode.RetainedListControl));
		_runPageScopedButton = CreateButton("Run page-scoped Observable control", () => RunAsync(ReproMode.PageScopedObservableControl));
		_clearButton = CreateButton("Clear retained state", ClearRetainedStateAsync);
		_stopButton = CreateButton("Stop", StopAsync);
		_stopButton.IsEnabled = false;

		_statusLabel = new Label
		{
			Text = "Use the default values first: 25 pages, 600 rows, 40 selected customers, 4 MB page payload.",
			FontSize = 14,
			TextColor = Color.FromArgb("#304256")
		};

		_summaryLabel = new Label
		{
			Text = "Run the retained List control, then run the leaky ObservableCollection scenario and compare live weak references after full GC.",
			FontSize = 13,
			LineBreakMode = LineBreakMode.WordWrap,
			TextColor = Color.FromArgb("#0B1F33")
		};

		_progress = new ProgressBar
		{
			Progress = 0
		};

		var parameters = new Grid
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

		parameters.Add(CreateNumberRow("Cycles", _cyclesEntry), 0, 0);
		parameters.Add(CreateNumberRow("Rows per page", _rowsEntry), 1, 0);
		parameters.Add(CreateNumberRow("Selected per page", _selectedEntry), 0, 1);
		parameters.Add(CreateNumberRow("Payload MB per page", _payloadEntry), 1, 1);
		parameters.Add(CreateNumberRow("Dwell ms", _dwellEntry), 0, 2);

		var commands = new Grid
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

		commands.Add(_runLeakyButton, 0, 0);
		commands.Add(_runRetainedListButton, 1, 0);
		commands.Add(_runPageScopedButton, 0, 1);
		commands.Add(_clearButton, 1, 1);
		commands.Add(_stopButton, 0, 2);

		var content = new VerticalStackLayout
		{
			Padding = new Thickness(24),
			Spacing = 18,
			Children =
			{
				new Label
				{
					Text = "SelectionList retained-selected-state leak",
					FontSize = 26,
					FontAttributes = FontAttributes.Bold,
					TextColor = Color.FromArgb("#0B1F33")
				},
				_statusLabel,
				parameters,
				commands,
				_progress,
				new Border
				{
					Stroke = Color.FromArgb("#C9D6DF"),
					StrokeThickness = 1,
					BackgroundColor = Color.FromArgb("#F7FAFC"),
					Padding = new Thickness(14),
					Content = _summaryLabel
				}
			}
		};

		Content = new ScrollView
		{
			Content = content
		};

#if AUTORUN_METRICS
		if (!s_autoRunStarted)
		{
			s_autoRunStarted = true;
			Dispatcher.Dispatch(async () => await RunAutomatedMetricsAsync());
		}
#endif
	}

	static Entry CreateNumberEntry(string text)
	{
		return new Entry
		{
			Text = text,
			Keyboard = Keyboard.Numeric,
			HorizontalTextAlignment = TextAlignment.End
		};
	}

	static View CreateNumberRow(string label, Entry entry)
	{
		var grid = new Grid
		{
			ColumnDefinitions =
			{
				new ColumnDefinition(GridLength.Star),
				new ColumnDefinition(new GridLength(110))
			},
			ColumnSpacing = 8
		};

		grid.Add(new Label
		{
			Text = label,
			VerticalTextAlignment = TextAlignment.Center,
			TextColor = Color.FromArgb("#304256")
		}, 0, 0);

		grid.Add(entry, 1, 0);

		return grid;
	}

	Button CreateButton(string text, Func<Task> action)
	{
		var button = new Button
		{
			Text = text,
			FontSize = 13,
			Padding = new Thickness(10, 8),
			MinimumHeightRequest = 44
		};

		button.Clicked += async (_, _) =>
		{
			try
			{
				await action();
			}
			catch (OperationCanceledException)
			{
				_statusLabel.Text = "Run stopped.";
			}
			catch (Exception ex)
			{
				_statusLabel.Text = ex.Message;
			}
		};

		return button;
	}

	async Task RunAsync(ReproMode mode)
	{
		if (_runCancellation is not null)
			return;

		var options = ReadOptions(mode);
		var cancellation = new CancellationTokenSource();
		_runCancellation = cancellation;
		SetRunning(true);
		_progress.Progress = 0;
		_summaryLabel.Text = string.Empty;

		SelectionStateStore.Reset();
		ReproSession.Current = null;

		_statusLabel.Text = "Collecting baseline...";
		_baseline = await MemorySampler.TakeAfterCollectionAsync();

		var session = new ReproSession(options);
		ReproSession.Current = session;

		try
		{
			for (var i = 0; i < options.Cycles; i++)
			{
				cancellation.Token.ThrowIfCancellationRequested();
				session.BeginNextCycle();

				_statusLabel.Text = string.Create(CultureInfo.InvariantCulture, $"Opening page {i + 1} of {options.Cycles}...");
				await Shell.Current.GoToAsync(AppShell.SelectionLeakRoute, animate: false);
				await Task.Delay(options.DwellMilliseconds, cancellation.Token);

				_statusLabel.Text = string.Create(CultureInfo.InvariantCulture, $"Closing page {i + 1} of {options.Cycles}...");
				await Shell.Current.GoToAsync("..", animate: false);
				await Task.Delay(25, cancellation.Token);

				_progress.Progress = (i + 1) / (double)options.Cycles;

				if ((i + 1) % 5 == 0 || i + 1 == options.Cycles)
					await UpdateSummaryAsync(session, "Collecting after full GC...");
			}

			await Task.Delay(1000, cancellation.Token);
			await UpdateSummaryAsync(session, "Complete. Full GC finished.");
		}
		finally
		{
			_runCancellation = null;
			SetRunning(false);
		}
	}

#if AUTORUN_METRICS
	async Task RunAutomatedMetricsAsync()
	{
		await Task.Delay(1000);

		try
		{
			MetricsLogger.Write("AUTORUN_START");

			foreach (var mode in new[]
			{
				ReproMode.RetainedListControl,
				ReproMode.PageScopedObservableControl,
				ReproMode.ObservableSelection
			})
			{
				MetricsLogger.Write($"SCENARIO_START {mode}");
				await RunAsync(mode);
				MetricsLogger.WriteBlock($"SCENARIO_RESULT {mode}", _summaryLabel.Text);
			}

			MetricsLogger.Write("CLEAR_RETAINED_STATE_START");
			await ClearRetainedStateAsync();
			MetricsLogger.WriteBlock("AFTER_CLEAR_RETAINED_STATE", _summaryLabel.Text);
			MetricsLogger.Write("AUTORUN_DONE");
			await Task.Delay(500);
			Application.Current?.Quit();
		}
		catch (Exception ex)
		{
			MetricsLogger.Write($"AUTORUN_ERROR {ex}");
			throw;
		}
	}
#endif

	ReproOptions ReadOptions(ReproMode mode)
	{
		var cycles = ReadBoundedInt(_cyclesEntry, 1, 200, 25);
		var rows = ReadBoundedInt(_rowsEntry, 1, 5000, 600);
		var selected = ReadBoundedInt(_selectedEntry, 0, rows, 40);
		var payload = ReadBoundedInt(_payloadEntry, 0, 64, 4);
		var dwell = ReadBoundedInt(_dwellEntry, 0, 5000, 60);

		return new ReproOptions(mode, cycles, rows, selected, payload, dwell);
	}

	static int ReadBoundedInt(Entry entry, int min, int max, int fallback)
	{
		if (!int.TryParse(entry.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
			value = fallback;

		value = Math.Clamp(value, min, max);
		entry.Text = value.ToString(CultureInfo.InvariantCulture);
		return value;
	}

	async Task UpdateSummaryAsync(ReproSession session, string status)
	{
		_statusLabel.Text = status;
		var current = await MemorySampler.TakeAfterCollectionAsync();
		_summaryLabel.Text = session.GetStats(_baseline, current).ToSummary();
	}

	async Task ClearRetainedStateAsync()
	{
		SelectionStateStore.Reset();
		await Task.Delay(1000);
		await MemorySampler.ForceFullCollectionAsync();

		if (ReproSession.Current is { } session)
		{
			var current = await MemorySampler.TakeAfterCollectionAsync();
			_summaryLabel.Text = session.GetStats(_baseline, current).ToSummary();
		}

		_statusLabel.Text = "Retained selection state cleared and full GC completed.";
	}

	Task StopAsync()
	{
		_runCancellation?.Cancel();
		return Task.CompletedTask;
	}

	void SetRunning(bool running)
	{
		_runLeakyButton.IsEnabled = !running;
		_runRetainedListButton.IsEnabled = !running;
		_runPageScopedButton.IsEnabled = !running;
		_clearButton.IsEnabled = !running;
		_stopButton.IsEnabled = running;
	}
}
