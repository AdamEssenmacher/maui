using Foundation;
using Microsoft.Maui;

namespace PhoneFlyoutPageRendererElementRetentionRepro;

[Register("AppDelegate")]
public sealed class AppDelegate : MauiUIApplicationDelegate
{
	protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();
}
