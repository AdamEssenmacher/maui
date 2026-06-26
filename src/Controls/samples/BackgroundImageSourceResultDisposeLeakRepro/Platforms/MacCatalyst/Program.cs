using UIKit;

namespace BackgroundImageSourceResultDisposeLeakRepro;

public static class Program
{
	static void Main(string[] args)
	{
		AutoRunSettings.Initialize(args);
		UIApplication.Main(args, null, typeof(AppDelegate));
	}
}
