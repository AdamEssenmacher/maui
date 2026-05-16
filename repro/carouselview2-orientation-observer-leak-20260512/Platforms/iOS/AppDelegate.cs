#if IOS
using Foundation;
using Microsoft.Maui;
using Microsoft.Maui.Hosting;

namespace CarouselView2OrientationObserverLeakRepro;

[Register("AppDelegate")]
public class AppDelegate : MauiUIApplicationDelegate
{
	protected override MauiApp CreateMauiApp()
	{
		return MauiProgram.CreateMauiApp();
	}
}
#endif
