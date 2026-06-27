using Microsoft.Maui.Controls;

namespace PHPickerNSItemProviderRetentionLeakRepro;

public sealed class App : Application
{
	protected override Window CreateWindow(IActivationState? activationState)
	{
		var page = new ContentPage
		{
			Content = new Label
			{
				Text = "Running PHPicker NSItemProvider retention probe...",
				HorizontalOptions = LayoutOptions.Center,
				VerticalOptions = LayoutOptions.Center
			}
		};

		page.Loaded += async (_, _) =>
		{
			await Task.Yield();
			var resultsPath = CommandLineOptions.GetResultsPath();
			try
			{
				var result = await PHPickerNSItemProviderRetentionProbe.RunAsync();
				File.WriteAllText(resultsPath, result.ToReport());
				Environment.Exit(result.ProvedLeak ? 0 : 2);
			}
			catch (Exception ex)
			{
				File.WriteAllText(resultsPath, ex.ToString());
				Environment.Exit(3);
			}
		};

		return new Window(page);
	}
}
