using Microsoft.Maui.ApplicationModel;

namespace MapElementsPerfRepro;

public sealed class DashboardPage : ContentPage
{
	readonly Entry _polylineCountEntry;
	readonly Entry _pointsPerPolylineEntry;
	readonly Entry _seedEntry;
	readonly Entry _watchdogEntry;
	readonly Entry _progressIntervalEntry;
	readonly Label _statusLabel;
	readonly Label _resultsPathLabel;
	bool _autoRunStarted;
	bool _manualRunStarted;

	public DashboardPage()
	{
		Title = "MapElements Perf Repro";
		BackgroundColor = Colors.White;

		_polylineCountEntry = CreateEntry("92");
		_pointsPerPolylineEntry = CreateEntry("500");
		_seedEntry = CreateEntry("20502");
		_watchdogEntry = CreateEntry("20");
		_progressIntervalEntry = CreateEntry("10");

		_statusLabel = new Label
		{
			Text = "Ready. Run the small baseline first to verify map setup, then run the issue repro.",
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
						Text = "MapElement polyline stress",
						FontSize = 22,
						FontAttributes = FontAttributes.Bold,
						TextColor = Color.FromArgb("#0B1F33")
					},
					new Label
					{
						Text = "This reproduces dotnet/maui#20502 by generating many polylines and adding them to Map.MapElements on the UI thread.",
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
				new RowDefinition(GridLength.Auto)
			},
			ColumnSpacing = 12,
			RowSpacing = 12
		};

		grid.Add(CreateField("Polylines", _polylineCountEntry), 0, 0);
		grid.Add(CreateField("Points/polyline", _pointsPerPolylineEntry), 1, 0);
		grid.Add(CreateField("Jitter seed", _seedEntry), 0, 1);
		grid.Add(CreateField("Watchdog seconds", _watchdogEntry), 1, 1);
		grid.Add(CreateField("Progress interval", _progressIntervalEntry), 0, 2);

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

		grid.Add(CreateButton("Run small baseline", () => RunManualAsync(CreateSmallBaselineOptions())), 0, 0);
		grid.Add(CreateButton("Run generation control", () => RunManualAsync(CreateGenerationControlOptions())), 1, 0);
		grid.Add(CreateButton("Run issue repro", () => RunManualAsync(CreateIssueReproOptions())), 0, 1);

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
			BackgroundColor = Color.FromArgb("#174A7C"),
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
		await Shell.Current.GoToAsync(AppShell.MapStressRoute);
	}

	async Task RunAutoAsync()
	{
		await RunScenarioAndReturnAsync(CreateSmallBaselineOptions());
		await RunScenarioAndReturnAsync(CreateGenerationControlOptions());
		await RunScenarioAndReturnAsync(CreateIssueReproOptions());
	}

	async Task<ReproResult> RunScenarioAndReturnAsync(ReproOptions options)
	{
		var session = new ReproSession(options);
		ReproSession.Current = session;
		_statusLabel.Text = $"Auto-running {options.Name}.";

		await Shell.Current.GoToAsync(AppShell.MapStressRoute);
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

	ReproOptions CreateSmallBaselineOptions()
	{
		return ReproOptions.CreateSmallBaseline(
			ReadBoundedInt(_seedEntry.Text, 1, int.MaxValue, 20502),
			ReadBoundedInt(_watchdogEntry.Text, 5, 300, 20),
			ReadBoundedInt(_progressIntervalEntry.Text, 1, 100, 10));
	}

	ReproOptions CreateGenerationControlOptions()
	{
		return ReproOptions.CreateGenerationControl(
			ReadBoundedInt(_polylineCountEntry.Text, 1, 2000, 92),
			ReadBoundedInt(_pointsPerPolylineEntry.Text, 1, 5000, 500),
			ReadBoundedInt(_seedEntry.Text, 1, int.MaxValue, 20502),
			ReadBoundedInt(_watchdogEntry.Text, 5, 300, 20),
			ReadBoundedInt(_progressIntervalEntry.Text, 1, 100, 10));
	}

	ReproOptions CreateIssueReproOptions()
	{
		return ReproOptions.CreateIssueRepro(
			ReadBoundedInt(_polylineCountEntry.Text, 1, 2000, 92),
			ReadBoundedInt(_pointsPerPolylineEntry.Text, 1, 5000, 500),
			ReadBoundedInt(_seedEntry.Text, 1, int.MaxValue, 20502),
			ReadBoundedInt(_watchdogEntry.Text, 5, 300, 20),
			ReadBoundedInt(_progressIntervalEntry.Text, 1, 100, 10));
	}

	static int ReadBoundedInt(string? text, int min, int max, int fallback)
	{
		if (!int.TryParse(text, out var value))
			value = fallback;

		return Math.Min(max, Math.Max(min, value));
	}
}
