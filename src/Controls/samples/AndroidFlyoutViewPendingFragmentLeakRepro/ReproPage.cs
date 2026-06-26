using Microsoft.Maui;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Platform;
using Microsoft.Maui.Storage;

namespace AndroidFlyoutViewPendingFragmentLeakRepro;

sealed class ReproPage : ContentPage
{
	readonly Label _statusLabel;
	bool _started;

	public ReproPage()
	{
		Title = "Pending Fragment Leak";

		_statusLabel = new Label
		{
			Text = "Waiting for Android activity...",
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

		var mauiContext = Handler?.MauiContext;
		var activity = MainActivity.Current;
		if (mauiContext is null || activity is null)
		{
			_statusLabel.Text = "No MauiContext/activity is available; repro did not run.";
			return;
		}

		var report = ReproSession.Run(mauiContext, mauiContext.GetFragmentManager());
		var text = report.ToText();
		var path = Path.Combine(FileSystem.AppDataDirectory, "autorun-results.txt");

		File.WriteAllText(path, text);
		_statusLabel.Text = $"{text}{Environment.NewLine}Result file: {path}";

		if (exitWhenFinished)
		{
			await Task.Delay(750);
			Android.OS.Process.KillProcess(Android.OS.Process.MyPid());
		}
	}
}
