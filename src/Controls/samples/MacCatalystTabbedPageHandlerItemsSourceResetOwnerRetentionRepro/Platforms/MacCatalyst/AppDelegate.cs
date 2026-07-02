using Foundation;
using Microsoft.Maui;

namespace MacCatalystTabbedPageHandlerItemsSourceResetOwnerRetentionRepro;

[Register("AppDelegate")]
public class AppDelegate : MauiUIApplicationDelegate
{
	protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();
}
