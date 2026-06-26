using Microsoft.Maui.Controls;

namespace MediaPickerUIImageFileResultRetentionLeakRepro;

public sealed class App : Application
{
	protected override Window CreateWindow(IActivationState? activationState)
	{
		var page = new ContentPage
		{
			Content = new Label
			{
				Text = "Running MediaPicker UIImage FileResult retention probe...",
				HorizontalOptions = LayoutOptions.Center,
				VerticalOptions = LayoutOptions.Center
			}
		};

		page.Loaded += async (_, _) =>
		{
			await Task.Yield();
			var resultsPath = CommandLineOptions.GetResultsPath();
			var result = await MediaPickerUIImageFileResultRetentionProbe.RunAsync();
			File.WriteAllText(resultsPath, result.ToReport());
			Environment.Exit(result.ProvedLeak ? 0 : 2);
		};

		return new Window(page);
	}
}
