using ObjCRuntime;
using UIKit;

namespace Maui.Controls.HybridWebViewRepro.Platform;

public class Program
{
	static void Main(string[] args)
	{
		UIApplication.Main(args, null, typeof(AppDelegate));
	}
}
