#nullable enable

using Microsoft.Maui.Controls;
using Microsoft.Maui;

namespace AndroidAccessibleTapDelegateRetentionRepro;

public partial class App : Application
{
	public App()
	{
		InitializeComponent();
	}

	protected override Window CreateWindow(IActivationState? activationState) =>
		new(new MainPage());
}
