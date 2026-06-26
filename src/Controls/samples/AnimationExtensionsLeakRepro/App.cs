using Microsoft.Maui;
using Microsoft.Maui.Controls;

namespace AnimationExtensionsLeakRepro;

public class App : Application
{
	protected override Window CreateWindow(IActivationState? activationState)
	{
		return new Window(new MainPage());
	}
}
