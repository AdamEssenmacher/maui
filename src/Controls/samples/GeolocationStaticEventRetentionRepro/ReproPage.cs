using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Storage;

namespace GeolocationStaticEventRetentionRepro;

sealed class ReproPage : ContentPage
{
	readonly Label _statusLabel;
	bool _started;

	public ReproPage()
	{
		Title = "Geolocation Static Event Retention";

		_statusLabel = new Label
		{
			Text = "Waiting to run repro...",
			FontFamily = "monospace",
			FontSize = 13,
			LineBreakMode = LineBreakMode.WordWrap
		};

		var runButton = new Button { Text = "Run Again" };
		runButton.Clicked += async (_, _) => await RunReproAsync(exitWhenFinished: false);

		Content = new ScrollView
		{
			Content = new VerticalStackLayout
			{
				Padding = new Thickness(16),
				Spacing = 12,
				Children =
				{
					_statusLabel,
					runButton
				}
			}
		};
	}

	protected override void OnAppearing()
	{
		base.OnAppearing();

		if (_started)
			return;

		_started = true;
		Dispatcher.Dispatch(async () => await RunReproAsync(exitWhenFinished: true));
	}

	async Task RunReproAsync(bool exitWhenFinished)
	{
		await Task.Delay(500);

		var report = ReproSession.Run();
		var text = report.ToText();
		var path = Path.Combine(FileSystem.AppDataDirectory, "geolocation-static-event-retention-results.txt");
#if ANDROID
		var fixedPath = Path.Combine(FileSystem.CacheDirectory, "maui-geolocation-static-event-retention-results.txt");
#else
		var fixedPath = "/tmp/maui-geolocation-static-event-retention-results.txt";
#endif

		File.WriteAllText(path, text);
		File.WriteAllText(fixedPath, text);
		Console.WriteLine(">>>GEOLOCATION_STATIC_EVENT_REPRO>>>");
		Console.WriteLine(text);
		Console.WriteLine($"Result file: {path}");
		Console.WriteLine($"Fixed result file: {fixedPath}");
		Console.WriteLine("<<<GEOLOCATION_STATIC_EVENT_REPRO<<<");

		_statusLabel.Text = $"{text}{Environment.NewLine}Result file: {path}{Environment.NewLine}Fixed result file: {fixedPath}";

		if (exitWhenFinished)
		{
			await Task.Delay(750);
#if ANDROID
			Android.OS.Process.KillProcess(Android.OS.Process.MyPid());
#else
			Environment.Exit(report.LeakProved ? 0 : 2);
#endif
		}
	}
}
