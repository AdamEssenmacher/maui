using Microsoft.Maui.Controls;

namespace IosCompatSliderRendererThumbImageRetentionRepro;

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
			Text = "Running iOS compatibility slider renderer thumb image retention repro...",
			HorizontalOptions = LayoutOptions.Center,
			VerticalOptions = LayoutOptions.Center
		};
	}

	protected override async void OnAppearing()
	{
		base.OnAppearing();
		await TryRunAsync();
	}

	protected override async void OnHandlerChanged()
	{
		base.OnHandlerChanged();
		await TryRunAsync();
	}

	async Task TryRunAsync()
	{
		if (_ran)
			return;

		if (Handler?.MauiContext is null)
			return;

		_ran = true;

		await Task.Delay(250);

		try
		{
			var report = await ReproSession.RunAsync(Handler.MauiContext);
			var text = report.ToText();
			File.WriteAllText(ReproSession.ResultsPath, text);
			Console.WriteLine(text);
		}
		catch (Exception ex)
		{
			var text = "IosCompatSliderRendererThumbImageRetentionRepro ERROR" + Environment.NewLine + ex;
			File.WriteAllText(ReproSession.ResultsPath, text);
			Console.WriteLine(text);
		}

		await Task.Delay(250);
		Environment.Exit(0);
	}
}
