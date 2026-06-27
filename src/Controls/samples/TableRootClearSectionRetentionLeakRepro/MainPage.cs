#nullable enable

using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Maui.Controls;

namespace TableRootClearSectionRetentionLeakRepro;

public sealed class MainPage : ContentPage
{
	readonly Label _status;

	public MainPage()
	{
		_status = new Label
		{
			Text = "Running TableRoot.Clear section-retention leak repro...",
			Margin = 24
		};

		Content = _status;
		Loaded += OnLoaded;
	}

	async void OnLoaded(object? sender, EventArgs e)
	{
		Loaded -= OnLoaded;

		var exitCode = 0;
		string text;
		try
		{
			var result = await ReproSession.RunAsync();
			text = result.ToText();
			exitCode = result.LeakProved ? 0 : 2;
		}
		catch (Exception ex)
		{
			text = "RESULT: FAILED" + Environment.NewLine + ex;
			exitCode = 3;
		}

		_status.Text = text;
		await WriteResultsAsync(text);
		await Task.Delay(250);
		Environment.Exit(exitCode);
	}

	static async Task WriteResultsAsync(string text)
	{
		var resultsPath = Environment.GetCommandLineArgs()
			.FirstOrDefault(static arg => arg.StartsWith("--results=", StringComparison.Ordinal))
			?.Substring("--results=".Length);

		resultsPath ??= Path.Combine(Path.GetTempPath(), "tablerootclearsectionretentionleakrepro-results.txt");

		Directory.CreateDirectory(Path.GetDirectoryName(resultsPath)!);
		await File.WriteAllTextAsync(resultsPath, text);
		Console.WriteLine(text);
	}
}
