using Microsoft.Maui.Controls;

namespace AndroidWebChromeClientCustomViewRetentionLeakRepro;

public sealed class App : Application
{
	public App()
	{
		MainPage = new NavigationPage(new MainPage());
	}
}
