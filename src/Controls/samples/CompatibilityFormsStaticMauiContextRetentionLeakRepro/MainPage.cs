#nullable enable
using Microsoft.Maui.Controls;

namespace CompatibilityFormsStaticMauiContextRetentionLeakRepro;

public sealed class MainPage : ContentPage
{
	bool _started;

	public MainPage()
	{
		Content = new Label
		{
			Text = "Running compatibility Forms static MauiContext retention leak repro...",
			HorizontalOptions = LayoutOptions.Center,
			VerticalOptions = LayoutOptions.Center
		};
	}

	protected override void OnAppearing()
	{
		base.OnAppearing();

		if (_started)
			return;

		_started = true;
		_ = RunAsync();
	}

	async Task RunAsync()
	{
		var report = await ReproSession.RunAsync();
		var text = report.ToText();

		if (Content is Label label)
			label.Text = text;

		if (TryGetResultsPath() is string path)
			await File.WriteAllTextAsync(path, text);

		Application.Current?.Quit();
	}

	static string? TryGetResultsPath()
	{
		const string prefix = "--results=";
		return Environment
			.GetCommandLineArgs()
			.FirstOrDefault(static arg => arg.StartsWith(prefix, StringComparison.Ordinal))
			?.Substring(prefix.Length);
	}
}
