namespace TableViewRootLeakRepro;

public sealed class DashboardPage : ContentPage
{
	readonly Entry _cyclesEntry;
	readonly Entry _payloadEntry;
	readonly Entry _dwellEntry;
	readonly Label _summaryLabel;
	readonly ProgressBar _progress;
	CancellationTokenSource? _runCancellation;
	MemorySnapshot _baseline = MemorySnapshot.Empty;
	bool _autoRunStarted;
	string _lastSummary = string.Empty;

	public DashboardPage()
	{
		Title = "TableViewRoot Leak Repro";
		BackgroundColor = Colors.White;

		_cyclesEntry = CreateEntry("40");
		_payloadEntry = CreateEntry("3");
		_dwellEntry = CreateEntry("25");
		_summaryLabel = new Label { Text = "Ready.", FontFamily = GetMonospaceFontFamily(), FontSize = 13, TextColor = Color.FromArgb("#172026") };
		_progress = new ProgressBar { HeightRequest = 6, ProgressColor = Color.FromArgb("#705C2E") };

		Content = new ScrollView
		{
			Content = new VerticalStackLayout
			{
				Padding = new Thickness(18, 18, 18, 28),
				Spacing = 14,
				Children =
				{
					new Label { Text = "Shared TableRoot retention", FontSize = 22, FontAttributes = FontAttributes.Bold, TextColor = Color.FromArgb("#0B1F33") },
					CreateField("Pages/run", _cyclesEntry),
					CreateField("Payload MB/page", _payloadEntry),
					CreateField("Dwell ms/page", _dwellEntry),
					CreateButton("Run control", () => RunAsync(ReproMode.FreshRootControl)),
					CreateButton("Run shared root", () => RunAsync(ReproMode.SharedRoot)),
					CreateButton("Run mitigation", () => RunAsync(ReproMode.ClearSharedRootOnDisappear)),
					_progress,
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

	static VerticalStackLayout CreateField(string title, Entry entry) => new()
	{
		Spacing = 4,
		Children =
		{
			new Label { Text = title, FontSize = 12, TextColor = Color.FromArgb("#57606A") },
			entry
		}
	};

	static Entry CreateEntry(string text) => new()
	{
		Text = text,
		Keyboard = Keyboard.Numeric,
		FontSize = 15,
		TextColor = Color.FromArgb("#172026"),
		BackgroundColor = Color.FromArgb("#F6F8FA")
	};

	static Button CreateButton(string text, Func<Task> action)
	{
		var button = new Button
		{
			Text = text,
			FontSize = 14,
			BackgroundColor = Color.FromArgb("#705C2E"),
			TextColor = Colors.White,
			CornerRadius = 6,
			MinimumHeightRequest = 44
		};

		button.Clicked += async (_, _) => await action();
		return button;
	}

	ReproOptions ReadOptions(ReproMode mode) => new(
		mode,
		ReadBoundedInt(_cyclesEntry.Text, 1, 200, 40),
		ReadBoundedInt(_payloadEntry.Text, 0, 64, 3),
		ReadBoundedInt(_dwellEntry.Text, 0, 5000, 25));

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
		var session = new ReproSession(options);
		ReproSession.Current = session;
		_progress.Progress = 0;

		try
		{
			_baseline = await MemorySampler.TakeAfterCollectionAsync();

			for (var i = 0; i < options.Cycles; i++)
			{
				token.ThrowIfCancellationRequested();
				session.BeginNextCycle();
				await Shell.Current.GoToAsync(AppShell.LeakRoute, animate: false);

				if (options.DwellMilliseconds > 0)
					await Task.Delay(options.DwellMilliseconds, token);

				_progress.Progress = (i + 1d) / (options.Cycles * 2d);
			}

			for (var i = 0; i < options.Cycles; i++)
			{
				token.ThrowIfCancellationRequested();
				await Shell.Current.GoToAsync("..", animate: false);
				await Task.Delay(25, token);
				_progress.Progress = (options.Cycles + i + 1d) / (options.Cycles * 2d);
			}

			var finalSnapshot = await MemorySampler.TakeAfterCollectionAsync();
			_summaryLabel.Text = session.GetStats(_baseline, finalSnapshot).ToSummary();
			_lastSummary = _summaryLabel.Text;
		}
		catch (Exception ex) when (ex is not OperationCanceledException)
		{
			_summaryLabel.Text = ex.ToString();
			_lastSummary = _summaryLabel.Text;
		}
		finally
		{
			ReproSession.Current = session;
			_runCancellation?.Dispose();
			_runCancellation = null;
		}
	}

	async Task RunAllScenariosAsync()
	{
		var report = new List<string>
		{
			$"TableViewRootLeakRepro autorun started at {DateTimeOffset.Now:O}",
			$"Defaults: pages={_cyclesEntry.Text}, payloadMB={_payloadEntry.Text}, dwellMs={_dwellEntry.Text}"
		};

		foreach (var mode in new[] { ReproMode.FreshRootControl, ReproMode.SharedRoot, ReproMode.ClearSharedRootOnDisappear })
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
		var paths = new[]
		{
			AutoRunSettings.ResultsPath,
			Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TableViewRootLeakRepro", "autorun-results.txt"),
			Path.Combine(Path.GetTempPath(), "tableviewrootleakrepro-results.txt")
		};

		foreach (var path in paths.Where(path => !string.IsNullOrWhiteSpace(path)).Select(path => path!).Distinct())
		{
			try
			{
				var directory = Path.GetDirectoryName(path);
				if (!string.IsNullOrWhiteSpace(directory))
					Directory.CreateDirectory(directory);

				await File.WriteAllTextAsync(path, reportText);
				return;
			}
			catch
			{
			}
		}
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
