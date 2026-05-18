using ObjCRuntime;
using UIKit;

namespace SwipeItemViewCommandLeakRepro.Platforms.iOS;

public static class Program
{
	static void Main(string[] args)
	{
		UIApplication.Main(args, null, typeof(AppDelegate));
	}
}
