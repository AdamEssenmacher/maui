namespace FormattedTextLeakRepro;

public sealed class DashboardPage : ContentPage
{
	readonly Entry _pagesEntry;
	readonly Entry _disclosuresEntry;
	readonly Entry _payloadEntry;
	readonly Entry _dwellEntry;
	readonly Button _runLeakButton;
	readonly Button _runControlButton;
	readonly Button _runMitigationButton;
	readonly Button _stopButton;
	readonly ProgressBar _progress;
	readonly Label _statusLabel;
	readonly Label _summaryLabel;
	CancellationTokenSource? _runCancellation;
	MemorySnapshot _baseline = MemorySnapshot.Empty;

	public DashboardPage()
	{
		Title = "FormattedText Leak Repro";
		BackgroundColor = Colors.White;

		_pagesEntry = CreateEntry("30");
		_disclosuresEntry = CreateEntry("24");
		_payloadEntry = CreateEntry("160");
		_dwellEntry = CreateEntry("25");

		_runLeakButton = CreateButton("Run shared resource", () => RunAsync(ReproMode.SharedResourceFormattedText));
		_runControlButton = CreateButton("Run inline control", () => RunAsync(ReproMode.InlineFormattedTextControl));
		_runMitigationButton = CreateButton("Run mitigation", () => RunAsync(ReproMode.ClearFormattedTextOnDisappear));
		_stopButton = CreateButton("Stop", StopRun);
		_stopButton.IsEnabled = false;

		_progress = new ProgressBar
		{
			Progress = 0,
			HeightRequest = 6,
			ProgressColor = Color.FromArgb("#0F766E")
		};

		_statusLabel = new Label
		{
			Text = "Ready. Run the inline control first, then the shared resource scenario.",
			TextColor = Color.FromArgb("#0F172A"),
			FontSize = 14
		};

		_summaryLabel = new Label
		{
			Text = "Defaults simulate 30 checkout/account screens with 24 disclosure labels each. Each label has a row view model with 160 KB of realistic retained state, so the shared-resource run can retain about 112 MB of view-model payload after the pages are popped.",
			TextColor = Color.FromArgb("#0F172A"),
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
						Text = "Shared FormattedText retention",
						FontSize = 22,
						FontAttributes = FontAttributes.Bold,
						TextColor = Color.FromArgb("#0F172A")
					},
						new Label
						{
							Text = "This app pushes real navigation pages, unwinds the stack, forces full GC, and counts which labels and row view models survived.",
							FontSize = 14,
							TextColor = Color.FromArgb("#475569")
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

		grid.Add(CreateField("Pages/run", _pagesEntry), 0, 0);
		grid.Add(CreateField("Disclosures/page", _disclosuresEntry), 1, 0);
		grid.Add(CreateField("Payload KB/disclosure", _payloadEntry), 0, 1);
		grid.Add(CreateField("Dwell ms/page", _dwellEntry), 1, 1);

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
					TextColor = Color.FromArgb("#475569")
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
			TextColor = Color.FromArgb("#0F172A"),
			BackgroundColor = Color.FromArgb("#F1F5F9")
		};
	}

	static Button CreateButton(string text, Func<Task> action)
	{
		var button = new Button
		{
			Text = text,
			FontSize = 14,
			BackgroundColor = Color.FromArgb("#0F766E"),
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
			ReadBoundedInt(_pagesEntry.Text, 1, 200, 30),
			ReadBoundedInt(_disclosuresEntry.Text, 1, 200, 24),
			ReadBoundedInt(_payloadEntry.Text, 0, 4096, 160),
			ReadBoundedInt(_dwellEntry.Text, 0, 5000, 25));
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
			for (var i = 0; i < options.Pages; i++)
			{
				token.ThrowIfCancellationRequested();
				var pageNumber = session.BeginNextPage();
				_statusLabel.Text = $"Pushing checkout page {pageNumber + 1}/{options.Pages}: {options.Name}";

				await Navigation.PushAsync(new LeakPage(), animated: false);

				if (options.DwellMilliseconds > 0)
					await Task.Delay(options.DwellMilliseconds, token);

				_progress.Progress = (i + 1d) / (options.Pages * 2d);
			}

			for (var i = 0; i < options.Pages; i++)
			{
				token.ThrowIfCancellationRequested();
				_statusLabel.Text = $"Popping checkout page {i + 1}/{options.Pages}: {options.Name}";

				var poppedPage = await Navigation.PopAsync(animated: false);
				var poppedContentPage = poppedPage as ContentPage;
				if (poppedContentPage is not null)
				{
					ReleasePoppedPage(poppedContentPage);
					poppedContentPage = null;
				}

				poppedPage = null;

				await Task.Delay(25, token);

				if ((i + 1) % 5 == 0 || i + 1 == options.Pages)
				{
					var current = await MemorySampler.TakeAfterCollectionAsync();
					_summaryLabel.Text = session.GetStats(_baseline, current).ToSummary();
				}

				_progress.Progress = (options.Pages + i + 1d) / (options.Pages * 2d);
			}

#if ANDROID
			await FlushAndroidNavigationRetentionAsync(token);
			_summaryLabel.Text = "Waiting for Android peer cleanup before final snapshot...";
			await Task.Delay(3000, token);
#endif
			var finalSnapshot = await MemorySampler.TakeAfterCollectionAsync();
			_summaryLabel.Text = session.GetStats(_baseline, finalSnapshot).ToSummary();
			_statusLabel.Text = $"Completed {options.Name}.";
		}
		catch (OperationCanceledException)
		{
			_statusLabel.Text = "Run stopped.";
		}
		catch (Exception ex)
		{
			_statusLabel.Text = "Run failed.";
			_summaryLabel.Text = ex.ToString();
		}
		finally
		{
			ReproSession.Current = session;
			_runCancellation?.Dispose();
			_runCancellation = null;
			SetRunning(false);
		}
	}

	static void ReleasePoppedPage(ContentPage page)
	{
		if (page.Content is IView content)
			content.DisconnectHandlers();

		page.Content = null;
		page.BindingContext = null;
	}

#if ANDROID
	async Task FlushAndroidNavigationRetentionAsync(CancellationToken token)
	{
		var sentinel = new ContentPage
		{
			Title = "Android cleanup",
			Content = new Label { Text = "Android cleanup page" }
		};

		await Navigation.PushAsync(sentinel, animated: false);
		await Task.Delay(25, token);

		var poppedPage = await Navigation.PopAsync(animated: false);
		if (poppedPage is ContentPage poppedContentPage)
			ReleasePoppedPage(poppedContentPage);
	}
#endif

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
