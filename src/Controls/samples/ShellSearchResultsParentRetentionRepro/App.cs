namespace ShellSearchResultsParentRetentionRepro;

public sealed class App : Application
{
	protected override Window CreateWindow(IActivationState? activationState)
	{
		AutoRunSettings.WriteStartupMarker("App.CreateWindow");

		var window = new Window(new ContentPage
		{
			Title = "Shell Search Results Parent Retention",
			Content = new Label
			{
				Text = "Running Shell search results parent retention probe...",
				HorizontalOptions = LayoutOptions.Center,
				VerticalOptions = LayoutOptions.Center
			}
		});

		_ = ShellSearchResultsParentRetentionProbe.RunAsync(window);
		return window;
	}
}
