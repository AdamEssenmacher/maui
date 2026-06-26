using UIKit;

namespace HostingServiceCollectionLeakRepro;

public class Program
{
	static void Main(string[] args)
	{
		UIApplication.Main(args, null, typeof(AppDelegate));
	}
}
