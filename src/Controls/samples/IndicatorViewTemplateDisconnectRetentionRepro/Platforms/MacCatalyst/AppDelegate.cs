using Foundation;
using Microsoft.Maui;
using Microsoft.Maui.Hosting;
using UIKit;

namespace IndicatorViewTemplateDisconnectRetentionRepro;

[Register("AppDelegate")]
public sealed class AppDelegate : MauiUIApplicationDelegate
{
	protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();

	public override bool FinishedLaunching(UIApplication application, NSDictionary? launchOptions)
	{
		var result = base.FinishedLaunching(application, launchOptions);

		try
		{
			var services = IPlatformApplication.Current?.Services
				?? throw new InvalidOperationException("Application services are not available.");
			var mauiContext = new MauiContext(services);
			var report = ReproSession.RunAsync(mauiContext).GetAwaiter().GetResult().ToText();
			File.WriteAllText(ReproSession.ResultsPath, report);
			Console.WriteLine(report);
			Environment.Exit(0);
		}
		catch (Exception ex)
		{
			File.WriteAllText(ReproSession.ResultsPath, ex.ToString());
			Console.Error.WriteLine(ex);
			Environment.Exit(2);
		}

		return result;
	}
}
