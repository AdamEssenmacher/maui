using Foundation;
using Microsoft.Maui;
using Microsoft.Maui.Hosting;

namespace XamlControlTemplateRootRetentionRepro;

[Register("AppDelegate")]
public class AppDelegate : MauiUIApplicationDelegate
{
	protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();
}
