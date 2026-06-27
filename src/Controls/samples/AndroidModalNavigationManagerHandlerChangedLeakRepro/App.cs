#nullable enable

using Microsoft.Maui;
using Microsoft.Maui.Controls;

namespace AndroidModalNavigationManagerHandlerChangedLeakRepro;

public sealed class App : Application
{
	protected override Window CreateWindow(IActivationState? activationState) =>
		new(new MainPage());
}
