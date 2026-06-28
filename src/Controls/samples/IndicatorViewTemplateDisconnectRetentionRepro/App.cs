using Microsoft.Maui;
using Microsoft.Maui.Controls;

namespace IndicatorViewTemplateDisconnectRetentionRepro;

public sealed class App : Application
{
	protected override Window CreateWindow(IActivationState? activationState)
	{
		return new Window(new ContentPage
		{
			Content = new Label
			{
				Text = "Running IndicatorView template disconnect retention repro...",
				HorizontalOptions = LayoutOptions.Center,
				VerticalOptions = LayoutOptions.Center
			}
		});
	}
}
