using Foundation;
using Microsoft.Maui;

namespace IosFlyoutPageLeftBarButtonAccessibilityRetentionRepro;

[Register("AppDelegate")]
public class AppDelegate : MauiUIApplicationDelegate
{
	protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();
}
