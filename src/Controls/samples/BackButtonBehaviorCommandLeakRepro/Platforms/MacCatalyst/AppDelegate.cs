using Foundation;
using Microsoft.Maui;
using Microsoft.Maui.Hosting;

namespace BackButtonBehaviorCommandLeakRepro;

[Register("AppDelegate")]
public sealed class AppDelegate : MauiUIApplicationDelegate
{
	protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();
}
