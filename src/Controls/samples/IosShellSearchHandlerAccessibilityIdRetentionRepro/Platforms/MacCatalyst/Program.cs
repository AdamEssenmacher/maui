using ObjCRuntime;
using UIKit;

namespace IosShellSearchHandlerAccessibilityIdRetentionRepro;

public static class Program
{
	static void Main(string[] args)
	{
		UIApplication.Main(args, null, typeof(AppDelegate));
	}
}
