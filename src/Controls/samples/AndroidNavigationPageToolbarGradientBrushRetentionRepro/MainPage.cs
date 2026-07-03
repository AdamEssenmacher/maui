using System;
using System.IO;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Storage;

namespace AndroidNavigationPageToolbarGradientBrushRetentionRepro;

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
		Title = "Android NavigationPage toolbar GradientBrush retention";

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
		try
		{
			var report = await ReproSession.RunAsync(this);
			var text = report.ToText();
			_resultsLabel.Text = text;

			var path = Path.Combine(FileSystem.AppDataDirectory, "autorun-results.txt");
			Directory.CreateDirectory(Path.GetDirectoryName(path)!);
			File.WriteAllText(path, text);
			Android.Util.Log.Info("AndroidNavigationPageToolbarGradientBrushRetentionRepro", text);
		}
		catch (Exception ex)
		{
			var text = "RESULT: ERROR" + Environment.NewLine + ex;
			_resultsLabel.Text = text;

			var path = Path.Combine(FileSystem.AppDataDirectory, "autorun-results.txt");
			Directory.CreateDirectory(Path.GetDirectoryName(path)!);
			File.WriteAllText(path, text);
			Android.Util.Log.Error("AndroidNavigationPageToolbarGradientBrushRetentionRepro", text);
		}
		finally
		{
			Android.OS.Process.KillProcess(Android.OS.Process.MyPid());
		}
	}
}
