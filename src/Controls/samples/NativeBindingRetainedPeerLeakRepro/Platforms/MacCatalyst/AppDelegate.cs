using Foundation;
using Microsoft.Maui;

namespace NativeBindingRetainedPeerLeakRepro;

[Register("AppDelegate")]
public sealed class AppDelegate : MauiUIApplicationDelegate
{
	protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();
}
