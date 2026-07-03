namespace ShellNavigationQueryParametersRetentionRepro;

public sealed class App : Application
{
	protected override Window CreateWindow(IActivationState? activationState)
	{
		AutoRunSettings.WriteStartupMarker("App.CreateWindow");

		var window = new Window(new ContentPage
		{
			Title = "Shell Query Parameters Retention",
			Content = new Label
			{
				Text = "Running ShellNavigationQueryParameters retention probe...",
				HorizontalOptions = LayoutOptions.Center,
				VerticalOptions = LayoutOptions.Center
			}
		});

		_ = ShellNavigationQueryParametersRetentionProbe.RunAsync(window);
		return window;
	}
}
