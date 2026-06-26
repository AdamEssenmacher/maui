using Foundation;
using Microsoft.Maui;

namespace StreamImageSourceUndisposedStreamLeakRepro.Platforms.MacCatalyst;

[Register("AppDelegate")]
public class AppDelegate : MauiUIApplicationDelegate
{
	protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();
}
