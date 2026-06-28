using Microsoft.Maui.Controls;

namespace MauiSwipeViewNativePeerRetentionRepro;

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
			Text = "Running MauiSwipeView native peer retention repro...",
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

		var context = Handler?.MauiContext ?? throw new InvalidOperationException("Runner page does not have a MauiContext.");
		var report = ReproSession.Run(context).ToText();
		File.WriteAllText(ReproSession.ResultsPath, report);
		Console.WriteLine(report);

		await Task.Delay(250);
		Environment.Exit(0);
	}
}
