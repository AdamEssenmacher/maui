using Microsoft.Maui.Controls;

namespace PHPickerProcessedOriginalRetentionLeakRepro;

public class App : Application
{
	public App()
	{
		_ = RunProbeAsync();
	}

	protected override Window CreateWindow(IActivationState? activationState) =>
		new(new ContentPage
		{
			Content = new Label
			{
				Text = "Running PHPicker processed-result retention probe...",
				HorizontalOptions = LayoutOptions.Center,
				VerticalOptions = LayoutOptions.Center
			}
		});

	static async Task RunProbeAsync()
	{
		var exitCode = 0;
		string report;

		try
		{
			var result = await PHPickerProcessedOriginalRetentionProbe.RunAsync();
			report = result.ToReport();
			exitCode = result.ProvedLeak ? 0 : 2;
		}
		catch (Exception ex)
		{
			report = ex.ToString();
			exitCode = 1;
		}

		Console.WriteLine(report);

		if (!string.IsNullOrWhiteSpace(CommandLineOptions.ResultsPath))
			await File.WriteAllTextAsync(CommandLineOptions.ResultsPath, report);

		Environment.Exit(exitCode);
	}
}
