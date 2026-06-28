using Foundation;
using Microsoft.Maui;

namespace PhoneFlyoutPageBackgroundPatternRetentionRepro;

[Register("AppDelegate")]
public sealed class AppDelegate : MauiUIApplicationDelegate
{
	protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();
}
