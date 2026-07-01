#nullable enable

using Microsoft.Maui.Controls;

namespace AndroidCompatWebViewRendererNativeDestroyRetentionRepro;

public sealed class App : Application
{
	public App()
	{
		MainPage = new MainPage();
	}
}
