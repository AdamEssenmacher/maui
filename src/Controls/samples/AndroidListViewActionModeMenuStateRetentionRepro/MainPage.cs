#nullable enable

using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Maui;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Storage;

namespace AndroidListViewActionModeMenuStateRetentionRepro;

public class MainPage : ContentPage
{
	readonly Label _status;

	public MainPage()
	{
		_status = new Label
		{
			Text = "Running Android ListView ActionMode menu-state retention repro...",
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
			var mauiContext = Handler?.MauiContext
				?? throw new InvalidOperationException("MainPage does not have a MAUI context.");
			var result = await ReproSession.RunAsync(mauiContext);
			text = result.ToText();
		}
		catch (Exception ex)
		{
			text = "RESULT: FAILED" + Environment.NewLine + ex;
		}

		_status.Text = text;
		WriteResult(text);

		await Task.Delay(250);
		Process.GetCurrentProcess().Kill();
	}

	static void WriteResult(string text)
	{
		var path = Path.Combine(FileSystem.AppDataDirectory, "autorun-results.txt");
		File.WriteAllText(path, text);
	}
}
