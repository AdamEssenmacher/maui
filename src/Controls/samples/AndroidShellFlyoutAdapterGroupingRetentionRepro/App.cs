using Microsoft.Maui.Controls;

namespace AndroidShellFlyoutAdapterGroupingRetentionRepro;

public sealed class App : Application
{
	protected override Window CreateWindow(IActivationState? activationState)
		=> new(new MainPage());
}
