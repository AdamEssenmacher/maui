using System;
using System.IO;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Storage;

namespace AndroidShellFlyoutContentRendererContentHandlerRetentionRepro;

public sealed class MainPage : ContentPage
{
	bool _hasRun;

	readonly Label _resultsLabel = new()
	{
		FontFamily = "monospace",
		FontSize = 12,
		LineBreakMode = Microsoft.Maui.LineBreakMode.WordWrap
	};

	public MainPage()
	{
		Title = "Android Shell flyout content handler retention";

		Content = new ScrollView
		{
			Content = new VerticalStackLayout
			{
				Padding = 24,
				Spacing = 16,
				Children =
				{
					new Button
					{
						Text = "Run repro",
						Command = new Command(RunRepro)
					},
					_resultsLabel
				}
			}
		};

		Loaded += (_, _) =>
		{
			if (_hasRun)
				return;

			_hasRun = true;
			Dispatcher.DispatchDelayed(TimeSpan.FromMilliseconds(250), RunRepro);
		};
	}

	async void RunRepro()
	{
		string text;
		try
		{
			var hostContext = Handler?.MauiContext ?? throw new InvalidOperationException("MauiContext is not available.");
			var report = await ReproSession.RunAsync(hostContext);
			text = report.ToText();
		}
		catch (Exception ex)
		{
			text = "RESULT: FAILED" + Environment.NewLine + ex;
		}

		_resultsLabel.Text = text;

		var path = Path.Combine(FileSystem.AppDataDirectory, "autorun-results.txt");
		Directory.CreateDirectory(Path.GetDirectoryName(path)!);
		File.WriteAllText(path, text);
		Android.Util.Log.Info("AndroidShellFlyoutContentRendererContentHandlerRetentionRepro", text);

		Android.OS.Process.KillProcess(Android.OS.Process.MyPid());
	}
}
