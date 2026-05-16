using Microsoft.Maui;
using Microsoft.Maui.Controls;

namespace CarouselView2OrientationObserverLeakRepro;

public class App : Application
{
	protected override Window CreateWindow(IActivationState? activationState)
	{
		return new Window(new NavigationPage(new LeakProbePage()));
	}
}
