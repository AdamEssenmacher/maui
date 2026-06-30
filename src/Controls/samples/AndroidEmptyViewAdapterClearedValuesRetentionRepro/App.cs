using Microsoft.Maui.Controls;

namespace AndroidEmptyViewAdapterClearedValuesRetentionRepro;

public sealed class App : Application
{
	public App()
	{
		MainPage = new NavigationPage(new MainPage());
	}
}
