using UIKit;

namespace MenuBarChildHandlerRetentionLeakRepro;

public class Program
{
	static void Main(string[] args)
	{
		UIApplication.Main(args, null, typeof(AppDelegate));
	}
}
