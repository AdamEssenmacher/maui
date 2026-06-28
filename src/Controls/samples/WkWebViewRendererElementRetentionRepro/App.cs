using Microsoft.Maui.Controls;

namespace WkWebViewRendererElementRetentionRepro;

public sealed class App : Application
{
	protected override Window CreateWindow(IActivationState? activationState)
	{
		return new Window(new RunnerPage());
	}
}

sealed class RunnerPage : ContentPage
{
	bool _ran;

	public RunnerPage()
	{
		Content = new Label
		{
			Text = "Running WkWebViewRenderer Element retention repro...",
			HorizontalOptions = LayoutOptions.Center,
			VerticalOptions = LayoutOptions.Center
		};
	}

	protected override async void OnAppearing()
	{
		base.OnAppearing();

		if (_ran)
			return;

		_ran = true;

		await Task.Delay(250);

		var report = ReproSession.Run().ToText();
		File.WriteAllText(ReproSession.ResultsPath, report);
		Console.WriteLine(report);

		await Task.Delay(250);
		Environment.Exit(0);
	}
}
