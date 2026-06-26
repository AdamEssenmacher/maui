using System;
using System.IO;
using Microsoft.Maui;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Storage;

namespace AndroidDragDropLocalStateRetentionLeakRepro;

public sealed class MainPage : ContentPage
{
	bool _hasRun;

	readonly Label _resultsLabel = new()
	{
		FontFamily = "monospace",
		FontSize = 12,
		LineBreakMode = LineBreakMode.WordWrap
	};

	public MainPage()
	{
		Title = "Android drag/drop local-state retention";

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
		var report = await ReproSession.RunAsync();
		var text = report.ToText();
		_resultsLabel.Text = text;

		var path = Path.Combine(FileSystem.AppDataDirectory, "autorun-results.txt");
		Directory.CreateDirectory(Path.GetDirectoryName(path)!);
		File.WriteAllText(path, text);
		Android.Util.Log.Info("AndroidDragDropLocalStateRetentionLeakRepro", text);

		Android.OS.Process.KillProcess(Android.OS.Process.MyPid());
	}
}
