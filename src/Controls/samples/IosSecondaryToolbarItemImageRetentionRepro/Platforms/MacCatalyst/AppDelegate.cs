using Foundation;
using Microsoft.Maui;

namespace IosSecondaryToolbarItemImageRetentionRepro;

[Register(nameof(AppDelegate))]
public sealed class AppDelegate : MauiUIApplicationDelegate
{
	protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();
}
