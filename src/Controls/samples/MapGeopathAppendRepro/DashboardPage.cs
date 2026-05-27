using Microsoft.Maui.ApplicationModel;

namespace MapGeopathAppendRepro;

public sealed class DashboardPage : ContentPage
{
	readonly Entry _initialPointCountEntry;
	readonly Entry _appendedPointCountEntry;
	readonly Entry _stepDelayEntry;
	readonly Label _statusLabel;
	readonly Label _resultsPathLabel;
	bool _autoRunStarted;
	bool _manualRunStarted;

	public DashboardPage()
	{
		Title = "Geopath Append Repro";
		BackgroundColor = Colors.White;

		_initialPointCountEntry = CreateEntry("2");
		_appendedPointCountEntry = CreateEntry("200");
		_stepDelayEntry = CreateEntry("0");

		_statusLabel = new Label
		{
			Text = "Ready. Defaults model a higher-than-average route redraw: 2 initial points plus 200 appended route updates.",
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
						Text = "Map Geopath append repro",
						FontSize = 22,
						FontAttributes = FontAttributes.Bold,
						TextColor = Color.FromArgb("#0B1F33")
					},
					new Label
					{
						Text = "This isolates retained Android PolylineOptions growth when a previously rendered route is mutated and then shown again. Results lead with unnecessary time, MB, and native point entries.",
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
				new RowDefinition(GridLength.Auto)
			},
			ColumnSpacing = 12,
			RowSpacing = 12
		};

		grid.Add(CreateField("Initial route points", _initialPointCountEntry), 0, 0);
		grid.Add(CreateField("Appended route points", _appendedPointCountEntry), 1, 0);
		grid.Add(CreateField("Step delay ms", _stepDelayEntry), 0, 1);

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

		grid.Add(CreateButton("Run fresh control", () => RunManualAsync(CreateFreshInstanceControlOptions())), 0, 0);
		grid.Add(CreateButton("Run Geopath.Add repro", () => RunManualAsync(CreateRetainedGeopathCollectionMutationOptions())), 1, 0);
		grid.Add(CreateButton("Run Polyline.Add repro", () => RunManualAsync(CreateRetainedPolylineAddMutationOptions())), 0, 1);

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
			BackgroundColor = Color.FromArgb("#0F5B4F"),
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
		await Shell.Current.GoToAsync(AppShell.MapMutationRoute);
	}

	async Task RunAutoAsync()
	{
		await RunScenarioAndReturnAsync(CreateFreshInstanceControlOptions());
		await RunScenarioAndReturnAsync(CreateRetainedGeopathCollectionMutationOptions());
		await RunScenarioAndReturnAsync(CreateRetainedPolylineAddMutationOptions());
	}

	async Task<ReproResult> RunScenarioAndReturnAsync(ReproOptions options)
	{
		var session = new ReproSession(options);
		ReproSession.Current = session;
		_statusLabel.Text = $"Auto-running {options.Name}.";

		await Shell.Current.GoToAsync(AppShell.MapMutationRoute);
		var result = await session.Completion.ConfigureAwait(false);

		await MainThread.InvokeOnMainThreadAsync(async () =>
		{
			_statusLabel.Text = result.ToDisplayText();

			if (Shell.Current.Navigation.NavigationStack.Count > 1)
				await Shell.Current.GoToAsync("..");
		});

		return result;
	}

	ReproOptions CreateFreshInstanceControlOptions()
	{
		return ReproOptions.CreateFreshInstanceControl(
			ReadBoundedInt(_initialPointCountEntry.Text, 1, 500, 2),
			ReadBoundedInt(_appendedPointCountEntry.Text, 1, 5000, 200),
			ReadBoundedInt(_stepDelayEntry.Text, 0, 1000, 0));
	}

	ReproOptions CreateRetainedGeopathCollectionMutationOptions()
	{
		return ReproOptions.CreateRetainedGeopathCollectionMutation(
			ReadBoundedInt(_initialPointCountEntry.Text, 1, 500, 2),
			ReadBoundedInt(_appendedPointCountEntry.Text, 1, 5000, 200),
			ReadBoundedInt(_stepDelayEntry.Text, 0, 1000, 0));
	}

	ReproOptions CreateRetainedPolylineAddMutationOptions()
	{
		return ReproOptions.CreateRetainedPolylineAddMutation(
			ReadBoundedInt(_initialPointCountEntry.Text, 1, 500, 2),
			ReadBoundedInt(_appendedPointCountEntry.Text, 1, 5000, 200),
			ReadBoundedInt(_stepDelayEntry.Text, 0, 1000, 0));
	}

	static int ReadBoundedInt(string? text, int min, int max, int fallback)
	{
		if (!int.TryParse(text, out var value))
			value = fallback;

		return Math.Min(max, Math.Max(min, value));
	}
}
