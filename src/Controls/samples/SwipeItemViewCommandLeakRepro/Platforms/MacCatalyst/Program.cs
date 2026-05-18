using ObjCRuntime;
using UIKit;

namespace SwipeItemViewCommandLeakRepro.Platforms.MacCatalyst;

public static class Program
{
	static void Main(string[] args)
	{
		UIApplication.Main(args, null, typeof(AppDelegate));
	}
}
