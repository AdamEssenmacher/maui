#nullable enable

using Microsoft.Maui;
using Microsoft.Maui.Controls;

namespace AndroidCoreContainerViewMauiContextRetentionRepro;

public sealed class App : Application
{
	protected override Window CreateWindow(IActivationState? activationState) =>
		new(new MainPage());
}
