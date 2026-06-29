#nullable enable

using System;
using System.IO;
using System.Threading.Tasks;
using AndroidX.AppCompat.App;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Storage;

namespace AndroidShellFlyoutBackgroundImageRetentionRepro;

public sealed class MainPage : ContentPage
{
	readonly Label _status;

	public MainPage()
	{
		_status = new Label
		{
			Text = "Running Android Shell flyout background image retention repro...",
			Margin = 24
		};

		Content = _status;
		Loaded += OnLoaded;
	}

	async void OnLoaded(object? sender, EventArgs e)
	{
		Loaded -= OnLoaded;

		string text;
		try
		{
			var activity = Platform.CurrentActivity as AppCompatActivity
				?? throw new InvalidOperationException("CurrentActivity is not an AppCompatActivity.");
			var result = await ReproSession.RunAsync(activity);
			text = result.ToText();
		}
		catch (Exception ex)
		{
			text = "RESULT: FAILED" + Environment.NewLine + ex;
		}

		_status.Text = text;
		await WriteResultsAsync(text);
		await Task.Delay(250);
		Environment.Exit(0);
	}

	static async Task WriteResultsAsync(string text)
	{
		var path = Path.Combine(FileSystem.AppDataDirectory, "autorun-results.txt");
		Directory.CreateDirectory(Path.GetDirectoryName(path)!);
		await File.WriteAllTextAsync(path, text);
		Console.WriteLine(text);
	}
}
