#nullable enable
using Microsoft.Maui.Controls;

namespace AndroidDefaultRendererMotionEventHelperRetentionLeakRepro;

public sealed class App : Application
{
	public App()
	{
		MainPage = new MainPage();
	}
}
