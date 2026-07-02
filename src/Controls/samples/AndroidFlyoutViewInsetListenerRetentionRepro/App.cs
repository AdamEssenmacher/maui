using Microsoft.Maui;
using Microsoft.Maui.Controls;

namespace AndroidFlyoutViewInsetListenerRetentionRepro;

public sealed class App : Application
{
	protected override Window CreateWindow(IActivationState? activationState)
	{
		return new Window(new ReproPage());
	}
}
