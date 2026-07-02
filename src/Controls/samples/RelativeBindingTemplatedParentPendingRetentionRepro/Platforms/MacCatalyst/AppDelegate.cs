using Foundation;
using Microsoft.Maui;

namespace RelativeBindingTemplatedParentPendingRetentionRepro;

[Register("AppDelegate")]
public class AppDelegate : MauiUIApplicationDelegate
{
	protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();
}
