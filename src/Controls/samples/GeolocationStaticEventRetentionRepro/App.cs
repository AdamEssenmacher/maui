using Microsoft.Maui;
using Microsoft.Maui.Controls;

namespace GeolocationStaticEventRetentionRepro;

public sealed class App : Microsoft.Maui.Controls.Application
{
	protected override Window CreateWindow(IActivationState? activationState)
	{
		return new Window(new ReproPage());
	}
}
