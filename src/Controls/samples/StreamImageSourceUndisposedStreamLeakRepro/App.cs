using Microsoft.Maui.Controls;

namespace StreamImageSourceUndisposedStreamLeakRepro;

public sealed class App : Application
{
	protected override Window CreateWindow(IActivationState? activationState)
	{
		var page = new ContentPage
		{
			Content = new Label
			{
				Text = "Running StreamImageSource undisposed stream probe...",
				HorizontalOptions = LayoutOptions.Center,
				VerticalOptions = LayoutOptions.Center
			}
		};

		page.Loaded += async (_, _) =>
		{
			await Task.Yield();
			var resultsPath = CommandLineOptions.GetResultsPath();
			var result = await StreamImageSourceUndisposedStreamProbe.RunAsync();
			File.WriteAllText(resultsPath, result.ToReport());
			Environment.Exit(result.ProvedLeak ? 0 : 2);
		};

		return new Window(page);
	}
}
