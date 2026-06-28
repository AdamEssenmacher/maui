using Microsoft.Maui.Controls;

namespace GraphicsViewDrawableRetentionRepro;

public sealed class App : Application
{
	protected override Window CreateWindow(IActivationState? activationState)
	{
		var page = new ContentPage
		{
			Content = new Label
			{
				Text = "Running GraphicsView drawable retention repro...",
				HorizontalOptions = LayoutOptions.Center,
				VerticalOptions = LayoutOptions.Center
			}
		};

		return new Window(page);
	}
}
