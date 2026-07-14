using Microsoft.Maui.Storage;

namespace AccelerometerEventRegressionRepro;

public sealed class DashboardPage : ContentPage
{
	readonly Button _runButton;
	readonly ActivityIndicator _activity;
	readonly Label _statusLabel;
	readonly Label _reportLabel;
	bool _running;
	bool _autoRunStarted;

	public DashboardPage()
	{
		Title = "Accelerometer Event Regression Repro";
		BackgroundColor = Colors.White;

		_runButton = new Button
		{
			Text = "Run retention probe",
			FontSize = 15,
			BackgroundColor = Color.FromArgb("#0F6B5B"),
			TextColor = Colors.White,
			CornerRadius = 6,
			MinimumHeightRequest = 46
		};
		_runButton.Clicked += async (_, _) => await RunAsync(autoRun: false);

		_activity = new ActivityIndicator
		{
			IsRunning = false,
			Color = Color.FromArgb("#0F6B5B")
		};

		_statusLabel = new Label
		{
			Text = "Ready. No physical sensor or Accelerometer.Start call is required.",
			TextColor = Color.FromArgb("#172026"),
			FontSize = 14
		};

		_reportLabel = new Label
		{
			Text = "The probe removes the exact composite delegate previously added, forces full collections, and checks whether the removed screen and its reachable state remain rooted by Accelerometer.Default.",
			TextColor = Color.FromArgb("#172026"),
			FontFamily = GetMonospaceFontFamily(),
			FontSize = 12,
			LineBreakMode = LineBreakMode.WordWrap
		};

		Content = new ScrollView
		{
			Content = new VerticalStackLayout
			{
				Padding = new Thickness(18, 18, 18, 28),
				Spacing = 14,
				Children =
				{
					new Label
					{
						Text = "Accelerometer exact-unsubscribe retention",
						FontSize = 22,
						FontAttributes = FontAttributes.Bold,
						TextColor = Color.FromArgb("#0B1F33")
					},
					new Label
					{
						Text = "Adds two composite ReadingChanged handlers that share an app-scoped final target, removes the exact first operand, and proves whether its screen-like target and 1 MiB reachable state can collect.",
						FontSize = 14,
						TextColor = Color.FromArgb("#57606A")
					},
					_runButton,
					_activity,
					_statusLabel,
					_reportLabel
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
			Dispatcher.Dispatch(async () => await RunAsync(autoRun: true));
		}
	}

	async Task RunAsync(bool autoRun)
	{
		if (_running)
			return;

		_running = true;
		_runButton.IsEnabled = false;
		_activity.IsRunning = true;
		_statusLabel.Text = "Running exact-unsubscribe and forced-GC checks...";
		_reportLabel.Text = "Please wait.";

		try
		{
			var report = await Task.Run(() => new AccelerometerProbe().Run());
			var reportText = report.ToText();

			Console.WriteLine(reportText);
			var writtenPaths = WriteResults(reportText);

			_reportLabel.Text = reportText;
			_statusLabel.Text = report.AffectedImplementationConfirmed
				? "Persistent retention regression confirmed. Result files: " + string.Join(", ", writtenPaths)
				: "Persistent retention signature was not present. Review the report and compare revisions.";

			if (autoRun)
			{
				await Task.Delay(250);
				Environment.Exit(report.ExitCode);
			}
		}
		catch (Exception exception)
		{
			var failure = $"RESULT: ERROR{Environment.NewLine}{exception}";
			Console.WriteLine(failure);
			_reportLabel.Text = failure;
			_statusLabel.Text = "Probe failed before producing a comparison result.";
			WriteResults(failure);

			if (autoRun)
			{
				await Task.Delay(250);
				Environment.Exit(3);
			}
		}
		finally
		{
			_running = false;
			_runButton.IsEnabled = true;
			_activity.IsRunning = false;
		}
	}

	static IReadOnlyList<string> WriteResults(string report)
	{
		var candidates = new List<string>();

		if (!string.IsNullOrWhiteSpace(AutoRunSettings.ResultsPath))
			candidates.Add(AutoRunSettings.ResultsPath);

		candidates.Add(Path.Combine(FileSystem.AppDataDirectory, "accelerometer-event-repro-results.txt"));
		candidates.Add(Path.Combine(Path.GetTempPath(), "accelerometer-event-repro-results.txt"));

		var written = new List<string>();

		foreach (var path in candidates.Distinct(StringComparer.Ordinal))
		{
			try
			{
				var directory = Path.GetDirectoryName(path);
				if (!string.IsNullOrWhiteSpace(directory))
					Directory.CreateDirectory(directory);

				File.WriteAllText(path, report);
				written.Add(path);
			}
			catch (Exception exception)
			{
				Console.WriteLine($"Could not write repro result to '{path}': {exception.Message}");
			}
		}

		return written;
	}

	static string GetMonospaceFontFamily()
	{
#if ANDROID
		return "monospace";
#elif IOS || MACCATALYST
		return "Menlo";
#else
		return "Courier New";
#endif
	}
}
