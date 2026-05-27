using Microsoft.Maui.ApplicationModel;

namespace MapElementsSyncPerfRepro;

public sealed class DashboardPage : ContentPage
{
	readonly Picker _elementKindPicker;
	readonly Entry _elementCountEntry;
	readonly Entry _seedEntry;
	readonly Entry _watchdogEntry;
	readonly Entry _observationEntry;
	readonly Entry _progressIntervalEntry;
	readonly Entry _mapSettleEntry;
	readonly Entry _pacedDelayEntry;
	readonly Label _statusLabel;
	readonly Label _resultsPathLabel;
	bool _autoRunStarted;
	bool _manualRunStarted;

	public DashboardPage()
	{
		Title = "MapElements Sync Perf Repro";
		BackgroundColor = Colors.White;

		_elementKindPicker = new Picker
		{
			Title = "Element kind",
			FontSize = 15,
			TextColor = Color.FromArgb("#172026"),
			BackgroundColor = Color.FromArgb("#F6F8FA")
		};
		_elementKindPicker.Items.Add(nameof(MapElementKind.Circle));
		_elementKindPicker.Items.Add(nameof(MapElementKind.ShortPolyline));
		_elementKindPicker.SelectedIndex = 0;

		_elementCountEntry = CreateEntry("1000");
		_seedEntry = CreateEntry("20502");
		_watchdogEntry = CreateEntry("45");
		_observationEntry = CreateEntry("8");
		_progressIntervalEntry = CreateEntry("100");
		_mapSettleEntry = CreateEntry("1500");
		_pacedDelayEntry = CreateEntry("1");

		_statusLabel = new Label
		{
			Text = "Ready. Compare detached populate with live burst and live paced adds.",
			TextColor = Color.FromArgb("#172026"),
			FontSize = 14
		};

		_resultsPathLabel = new Label
		{
			Text = $"Results: {AutoRunSettings.GetResultsPath()}",
			TextColor = Color.FromArgb("#57606A"),
			FontSize = 12,
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
						Text = "MapElements collection-sync stress",
						FontSize = 22,
						FontAttributes = FontAttributes.Bold,
						TextColor = Color.FromArgb("#0B1F33")
					},
					new Label
					{
						Text = "This isolates repeated Map.MapElements synchronization by using many low-cost map elements and comparing detached population with live incremental adds.",
						FontSize = 14,
						TextColor = Color.FromArgb("#57606A")
					},
					CreateSettingsGrid(),
					CreateButtonGrid(),
					_statusLabel,
					_resultsPathLabel
				}
			}
		};
	}

	protected override void OnAppearing()
	{
		base.OnAppearing();
		_manualRunStarted = false;

		if (AutoRunSettings.Enabled && !_autoRunStarted)
		{
			_autoRunStarted = true;
			AutoRunSettings.ResetResultsFile();
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
				new RowDefinition(GridLength.Auto),
				new RowDefinition(GridLength.Auto),
				new RowDefinition(GridLength.Auto)
			},
			ColumnSpacing = 12,
			RowSpacing = 12
		};

		grid.Add(CreateField("Element kind", _elementKindPicker), 0, 0);
		grid.Add(CreateField("Elements", _elementCountEntry), 1, 0);
		grid.Add(CreateField("Seed", _seedEntry), 0, 1);
		grid.Add(CreateField("Watchdog seconds", _watchdogEntry), 1, 1);
		grid.Add(CreateField("Observation seconds", _observationEntry), 0, 2);
		grid.Add(CreateField("Progress interval", _progressIntervalEntry), 1, 2);
		grid.Add(CreateField("Live map settle ms", _mapSettleEntry), 0, 3);
		grid.Add(CreateField("Paced add delay ms", _pacedDelayEntry), 1, 3);

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
				new RowDefinition(GridLength.Auto),
				new RowDefinition(GridLength.Auto)
			},
			ColumnSpacing = 12,
			RowSpacing = 12
		};

		grid.Add(CreateButton("Generation control", () => RunManualAsync(CreateOptions(ReproScenario.GenerationControl))), 0, 0);
		grid.Add(CreateButton("Detached populate", () => RunManualAsync(CreateOptions(ReproScenario.DetachedPopulate))), 1, 0);
		grid.Add(CreateButton("Live burst add", () => RunManualAsync(CreateOptions(ReproScenario.LiveBurstAdd))), 0, 1);
		grid.Add(CreateButton("Live paced add", () => RunManualAsync(CreateOptions(ReproScenario.LivePacedAdd))), 1, 1);
		var suiteButton = CreateButton("Run before/after suite", RunImpactSuiteAsync);
		grid.Add(suiteButton, 0, 2);
		Grid.SetColumnSpan(suiteButton, 2);

		return grid;
	}

	static VerticalStackLayout CreateField(string title, View input)
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
				input
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
			BackgroundColor = Color.FromArgb("#245B4E"),
			TextColor = Colors.White,
			CornerRadius = 6,
			MinimumHeightRequest = 44
		};

		button.Clicked += async (_, _) => await action();
		return button;
	}

	async Task RunManualAsync(ReproOptions options)
	{
		if (_manualRunStarted)
			return;

		_manualRunStarted = true;
		_statusLabel.Text = $"Starting {options.Name}.";

		ReproSession.Current = new ReproSession(options);
		await Shell.Current.GoToAsync(AppShell.SyncStressRoute);
	}

	async Task RunAutoAsync()
	{
		await RunImpactSuiteCoreAsync(resetResultsFile: false);
	}

	async Task RunImpactSuiteAsync()
	{
		if (_manualRunStarted)
			return;

		_manualRunStarted = true;
		await RunImpactSuiteCoreAsync(resetResultsFile: true);
	}

	async Task RunImpactSuiteCoreAsync(bool resetResultsFile)
	{
		if (resetResultsFile)
			AutoRunSettings.ResetResultsFile();

		var results = new List<ReproResult>();
		var scenarios = new[]
		{
			ReproScenario.GenerationControl,
			ReproScenario.DetachedPopulate,
			ReproScenario.LiveBurstAdd,
			ReproScenario.LivePacedAdd
		};

		foreach (var scenario in scenarios)
		{
			var result = await RunScenarioAndReturnAsync(CreateOptions(scenario));
			results.Add(result);

			if (result.Status is ReproStatus.Hung or ReproStatus.Failed)
				break;
		}

		var summary = ImpactSummary.Create(results);
		AutoRunSettings.AppendTextBlock("Impact summary", summary);
		await MainThread.InvokeOnMainThreadAsync(() =>
		{
			_statusLabel.Text = summary;
			_manualRunStarted = false;
		});
	}

	async Task<ReproResult> RunScenarioAndReturnAsync(ReproOptions options)
	{
		var session = new ReproSession(options);
		ReproSession.Current = session;
		await MainThread.InvokeOnMainThreadAsync(() =>
		{
			_statusLabel.Text = $"Auto-running {options.Name}.";
		});

		await MainThread.InvokeOnMainThreadAsync(async () =>
		{
			await Shell.Current.GoToAsync(AppShell.SyncStressRoute);
		});
		var result = await session.Completion.ConfigureAwait(false);

		if (result.Status == ReproStatus.Hung)
			return result;

		await MainThread.InvokeOnMainThreadAsync(async () =>
		{
			_statusLabel.Text = result.ToDisplayText();

			if (Shell.Current.Navigation.NavigationStack.Count > 1)
				await Shell.Current.GoToAsync("..");
		});

		return result;
	}

	ReproOptions CreateOptions(ReproScenario scenario)
	{
		var elementKind = _elementKindPicker.SelectedIndex == 1
			? MapElementKind.ShortPolyline
			: MapElementKind.Circle;
		var elementCount = ReadBoundedInt(_elementCountEntry.Text, 1, 10000, 1000);
		var seed = ReadBoundedInt(_seedEntry.Text, 1, int.MaxValue, 20502);
		var watchdogTimeoutSeconds = ReadBoundedInt(_watchdogEntry.Text, 5, 600, 45);
		var observationSeconds = ReadBoundedInt(_observationEntry.Text, 0, 120, 8);
		var progressInterval = ReadBoundedInt(_progressIntervalEntry.Text, 1, 5000, 100);
		var liveMapSettleMilliseconds = ReadBoundedInt(_mapSettleEntry.Text, 0, 15000, 1500);
		var pacedAddDelayMilliseconds = ReadBoundedInt(_pacedDelayEntry.Text, 0, 2000, 1);

		return scenario switch
		{
			ReproScenario.GenerationControl => ReproOptions.CreateGenerationControl(
				elementKind,
				elementCount,
				seed,
				watchdogTimeoutSeconds,
				observationSeconds,
				progressInterval,
				liveMapSettleMilliseconds,
				pacedAddDelayMilliseconds),
			ReproScenario.DetachedPopulate => ReproOptions.CreateDetachedPopulate(
				elementKind,
				elementCount,
				seed,
				watchdogTimeoutSeconds,
				observationSeconds,
				progressInterval,
				liveMapSettleMilliseconds,
				pacedAddDelayMilliseconds),
			ReproScenario.LiveBurstAdd => ReproOptions.CreateLiveBurstAdd(
				elementKind,
				elementCount,
				seed,
				watchdogTimeoutSeconds,
				observationSeconds,
				progressInterval,
				liveMapSettleMilliseconds,
				pacedAddDelayMilliseconds),
			ReproScenario.LivePacedAdd => ReproOptions.CreateLivePacedAdd(
				elementKind,
				elementCount,
				seed,
				watchdogTimeoutSeconds,
				observationSeconds,
				progressInterval,
				liveMapSettleMilliseconds,
				pacedAddDelayMilliseconds),
			_ => throw new ArgumentOutOfRangeException(nameof(scenario), scenario, null)
		};
	}

	static int ReadBoundedInt(string? text, int min, int max, int fallback)
	{
		if (!int.TryParse(text, out var value))
			value = fallback;

		return Math.Min(max, Math.Max(min, value));
	}
}
