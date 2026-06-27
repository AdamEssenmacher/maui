using Microsoft.Maui;
using Microsoft.Maui.Controls;

namespace AndroidFastRendererElementRetentionLeakRepro;

public sealed class App : Application
{
	protected override Window CreateWindow(IActivationState activationState)
	{
		return new Window(new MainPage());
	}
}
