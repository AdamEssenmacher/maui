using UIKit;

namespace Maui.Controls.Sample.TabbedPageBarBackgroundLeakRepro.Platforms.iOS;

public class Program
{
	static void Main(string[] args) => UIApplication.Main(args, null, typeof(AppDelegate));
}
