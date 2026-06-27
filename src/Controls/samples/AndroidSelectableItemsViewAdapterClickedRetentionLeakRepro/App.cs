using Microsoft.Maui.Controls;

namespace AndroidSelectableItemsViewAdapterClickedRetentionLeakRepro;

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
				Text = "Running Android SelectableItemsViewAdapter clicked-retention probe...",
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
			var result = AndroidSelectableItemsViewAdapterClickedRetentionProbe.Run();
			report = result.ToReport();
			exitCode = result.ProvedLeak ? 0 : 2;
		}
		catch (Exception ex)
		{
			report = ex.ToString();
			exitCode = 1;
		}

		Console.WriteLine(report);

#if ANDROID
		var resultPath = Path.Combine(Android.App.Application.Context.FilesDir!.AbsolutePath, "autorun-results.txt");
		await File.WriteAllTextAsync(resultPath, report);
#endif

		Environment.Exit(exitCode);
	}
}
