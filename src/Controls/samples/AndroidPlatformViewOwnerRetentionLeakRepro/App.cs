using Microsoft.Maui.Controls;

namespace AndroidPlatformViewOwnerRetentionLeakRepro;

public sealed class App : Application
{
	public App()
	{
		MainPage = new NavigationPage(new MainPage());
	}
}
