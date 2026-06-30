using Foundation;
using Microsoft.Maui;

namespace IosVisualElementRendererMauiContextRetentionRepro;

[Register("AppDelegate")]
public class AppDelegate : MauiUIApplicationDelegate
{
	protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();
}
