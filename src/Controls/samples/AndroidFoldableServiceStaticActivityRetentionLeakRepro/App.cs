using Microsoft.Maui;
using Microsoft.Maui.Controls;

namespace AndroidFoldableServiceStaticActivityRetentionLeakRepro;

public sealed class App : Application
{
	protected override Window CreateWindow(IActivationState activationState)
	{
		return new Window(new MainPage());
	}
}
