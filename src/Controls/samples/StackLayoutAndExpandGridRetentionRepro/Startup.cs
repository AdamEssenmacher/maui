using Foundation;
using UIKit;

namespace StackLayoutAndExpandGridRetentionRepro;

public static class Startup
{
	static void Main(string[] args)
	{
		UIApplication.Main(args, null, typeof(AppDelegate));
	}
}

[Register(nameof(AppDelegate))]
public sealed class AppDelegate : MauiUIApplicationDelegate
{
	protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();
}
