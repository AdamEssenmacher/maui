using Microsoft.Maui.Controls;

namespace IosShareSheetItemSourceRetentionRepro;

public sealed class App : Application
{
	protected override Window CreateWindow(IActivationState? activationState)
	{
		var page = new ContentPage
		{
			Content = new Label
			{
				Text = "Running iOS Share sheet item-source retention repro",
				HorizontalOptions = LayoutOptions.Center,
				VerticalOptions = LayoutOptions.Center
			}
		};

		page.Dispatcher.Dispatch(() =>
		{
			try
			{
				Thread.Sleep(250);
				var report = ReproSession.Run();
				File.WriteAllText(ReproSession.ResultsPath, report.ToText());
			}
			catch (Exception ex)
			{
				File.WriteAllText(ReproSession.ResultsPath, ex.ToString());
			}
			finally
			{
				Environment.Exit(0);
			}
		});

		return new Window(page);
	}
}
