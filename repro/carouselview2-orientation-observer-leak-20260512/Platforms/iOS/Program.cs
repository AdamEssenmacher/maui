#if IOS
using UIKit;

namespace CarouselView2OrientationObserverLeakRepro;

public class Program
{
	static void Main(string[] args)
	{
		UIApplication.Main(args, null, typeof(AppDelegate));
	}
}
#endif
