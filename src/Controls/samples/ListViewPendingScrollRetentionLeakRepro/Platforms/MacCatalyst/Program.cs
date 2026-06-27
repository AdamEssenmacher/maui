using UIKit;

namespace ListViewPendingScrollRetentionLeakRepro;

public class Program
{
	static void Main(string[] args)
	{
		UIApplication.Main(args, null, typeof(AppDelegate));
	}
}
