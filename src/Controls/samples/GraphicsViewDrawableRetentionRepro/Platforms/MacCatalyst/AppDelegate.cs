using Foundation;
using Microsoft.Maui;
using Microsoft.Maui.Hosting;
using UIKit;

namespace GraphicsViewDrawableRetentionRepro;

[Register("AppDelegate")]
public class AppDelegate : MauiUIApplicationDelegate
{
	protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();

	public override bool FinishedLaunching(UIApplication application, NSDictionary? launchOptions)
	{
		var result = base.FinishedLaunching(application, launchOptions);
		var report = ReproSession.RunAsync().GetAwaiter().GetResult().ToText();
		File.WriteAllText(ReproSession.ResultsPath, report);
		Console.WriteLine(report);
		Environment.Exit(0);
		return result;
	}
}
