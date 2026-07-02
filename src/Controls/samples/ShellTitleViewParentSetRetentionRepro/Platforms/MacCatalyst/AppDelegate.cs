using Foundation;
using Microsoft.Maui;
using Microsoft.Maui.Hosting;
using ShellTitleViewParentSetRetentionRepro;
using UIKit;

namespace ShellTitleViewParentSetRetentionRepro.Platforms.MacCatalyst;

[Register("AppDelegate")]
public class AppDelegate : MauiUIApplicationDelegate
{
	public override bool FinishedLaunching(UIApplication application, NSDictionary? launchOptions)
	{
		var result = base.FinishedLaunching(application, launchOptions);
		ShellTitleViewParentSetRetentionProbe.Schedule(nameof(FinishedLaunching));
		return result;
	}

	protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();
}
