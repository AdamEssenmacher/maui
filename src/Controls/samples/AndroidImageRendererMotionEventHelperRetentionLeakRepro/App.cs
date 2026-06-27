#nullable enable
using Microsoft.Maui.Controls;

namespace AndroidImageRendererMotionEventHelperRetentionLeakRepro;

public sealed class App : Application
{
	public App()
	{
		MainPage = new MainPage();
	}
}
