#nullable enable

using Microsoft.Maui;
using Microsoft.Maui.Controls;

namespace AndroidShellSearchViewReloadLeakRepro;

public partial class App : Application
{
	public App()
	{
		InitializeComponent();
	}

	protected override Window CreateWindow(IActivationState? activationState) =>
		new(new MainPage());
}
