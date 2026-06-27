#nullable enable
using Microsoft.Maui.Controls;

namespace AndroidBoxRendererMotionEventHelperRetentionLeakRepro;

public sealed class App : Application
{
	public App()
	{
		MainPage = new MainPage();
	}
}
