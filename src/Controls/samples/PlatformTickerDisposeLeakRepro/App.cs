using Microsoft.Maui;
using Microsoft.Maui.Controls;

namespace PlatformTickerDisposeLeakRepro;

public class App : Application
{
	protected override Window CreateWindow(IActivationState? activationState)
	{
		return new Window(new MainPage());
	}
}
