using UIKit;

namespace ScreenshotResultNativeImageRetentionLeakRepro.Platforms.MacCatalyst;

public static class Program
{
	static void Main(string[] args) => UIApplication.Main(args, null, typeof(AppDelegate));
}
