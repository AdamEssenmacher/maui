using Microsoft.Maui.Controls;

namespace ShellSectionRendererCompletionTasksRetentionRepro;

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
			Text = "Running ShellSectionRenderer completion task retention repro...",
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

		var context = Handler?.MauiContext ?? throw new InvalidOperationException("Runner page has no MAUI context.");
		var report = await ReproSession.RunAsync(context);
		File.WriteAllText(ReproSession.ResultsPath, report);
		Console.WriteLine(report);

		await Task.Delay(250);
		Environment.Exit(0);
	}
}
