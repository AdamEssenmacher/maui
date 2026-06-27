#nullable enable
using Microsoft.Maui.Controls;

namespace AndroidMapsStaticBundleRetentionLeakRepro;

public sealed class App : Application
{
	protected override Window CreateWindow(Microsoft.Maui.IActivationState? activationState)
	{
		return new Window(new MainPage());
	}
}
