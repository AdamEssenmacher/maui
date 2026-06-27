using Microsoft.Maui.Controls;

namespace AndroidMauiRecyclerViewElementRetentionLeakRepro;

public sealed class App : Application
{
	public App()
	{
		MainPage = new NavigationPage(new MainPage());
	}
}
