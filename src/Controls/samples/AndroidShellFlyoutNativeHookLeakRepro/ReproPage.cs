using Microsoft.Maui;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Storage;

namespace AndroidShellFlyoutNativeHookLeakRepro;

sealed class ReproPage : ContentPage
{
	readonly ReproShell _shell;
	readonly Label _statusLabel;
	bool _started;

	public ReproPage(ReproShell shell)
	{
		_shell = shell;
		Title = "Run";

		_statusLabel = new Label
		{
			Text = "Waiting for Android Shell handler...",
			FontFamily = "monospace",
			FontSize = 13,
			LineBreakMode = LineBreakMode.WordWrap
		};

		var runButton = new Button
		{
			Text = "Run Again"
		};
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

		var mauiContext = _shell.Handler?.MauiContext ?? Handler?.MauiContext;
		if (mauiContext is null)
		{
			_statusLabel.Text = "No MauiContext is available; repro did not run.";
			return;
		}

		var report = ReproSession.Run(mauiContext, _shell);
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
