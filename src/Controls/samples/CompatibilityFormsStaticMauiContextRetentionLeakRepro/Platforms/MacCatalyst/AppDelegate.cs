using Foundation;
using Microsoft.Maui;
using Microsoft.Maui.Hosting;

namespace CompatibilityFormsStaticMauiContextRetentionLeakRepro;

[Register("AppDelegate")]
public sealed class AppDelegate : MauiUIApplicationDelegate
{
	protected override MauiApp CreateMauiApp()
	{
		return MauiProgram.CreateMauiApp();
	}
}
