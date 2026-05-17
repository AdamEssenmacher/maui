using Foundation;
using Microsoft.Maui;
using Microsoft.Maui.Hosting;

namespace SwipeItemsLeakRepro.Platforms.iOS;

[Register("AppDelegate")]
public class AppDelegate : MauiUIApplicationDelegate
{
	protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();
}
