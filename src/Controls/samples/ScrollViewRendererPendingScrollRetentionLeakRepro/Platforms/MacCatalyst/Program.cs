using UIKit;

namespace ScrollViewRendererPendingScrollRetentionLeakRepro;

public class Program
{
	static void Main(string[] args)
	{
		UIApplication.Main(args, null, typeof(AppDelegate));
	}
}
