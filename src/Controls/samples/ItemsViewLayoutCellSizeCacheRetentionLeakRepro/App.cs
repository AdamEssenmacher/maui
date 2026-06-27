using Microsoft.Maui.Controls;

namespace ItemsViewLayoutCellSizeCacheRetentionLeakRepro;

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
				Text = "Running ItemsViewLayout cell-size cache retention probe...",
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
			var result = ItemsViewLayoutCellSizeCacheRetentionProbe.Run();
			report = result.ToReport();
			exitCode = result.ProvedLeak ? 0 : 2;
		}
		catch (Exception ex)
		{
			report = ex.ToString();
			exitCode = 1;
		}

		Console.WriteLine(report);

		var resultPath = Environment.GetCommandLineArgs()
			.Select(arg => arg.StartsWith("--results=", StringComparison.Ordinal) ? arg["--results=".Length..] : null)
			.FirstOrDefault(path => !string.IsNullOrWhiteSpace(path));

		if (resultPath is not null)
			await File.WriteAllTextAsync(resultPath, report);

		Environment.Exit(exitCode);
	}
}
