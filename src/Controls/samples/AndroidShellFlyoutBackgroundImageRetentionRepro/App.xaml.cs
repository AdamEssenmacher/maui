#nullable enable

using Microsoft.Maui.Controls;

namespace AndroidShellFlyoutBackgroundImageRetentionRepro;

public partial class App : Application
{
	public App()
	{
		InitializeComponent();
		MainPage = new MainPage();
	}
}
