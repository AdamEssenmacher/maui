namespace AndroidActivityResultLauncherLeakRepro;

public sealed class App : Application
{
	protected override Window CreateWindow(IActivationState? activationState) =>
		new(new ContentPage
		{
			Content = new Label
			{
				Text = "Running Android ActivityResultLauncher leak repro...",
				HorizontalOptions = LayoutOptions.Center,
				VerticalOptions = LayoutOptions.Center
			}
		});
}
