#nullable enable

using Microsoft.Maui.Controls;
using Microsoft.Maui;

namespace AndroidHybridWebViewNativeDestroyRetentionRepro;

public sealed class App : Application
{
	protected override Window CreateWindow(IActivationState? activationState) =>
		new(new MainPage());
}
