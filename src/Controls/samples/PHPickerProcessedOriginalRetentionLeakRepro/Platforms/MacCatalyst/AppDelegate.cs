using Foundation;
using Microsoft.Maui;

namespace PHPickerProcessedOriginalRetentionLeakRepro;

[Register("AppDelegate")]
public class AppDelegate : MauiUIApplicationDelegate
{
	protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();
}
