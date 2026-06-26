using Foundation;
using Microsoft.Maui;

namespace AnimationExtensionsLeakRepro;

[Register("AppDelegate")]
public class AppDelegate : MauiUIApplicationDelegate
{
	protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();
}
