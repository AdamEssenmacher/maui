using UIKit;

namespace MapGeopathAppendRepro;

public class Program
{
	static void Main(string[] args)
	{
		AutoRunSettings.Initialize(args);
		UIApplication.Main(args, null, typeof(AppDelegate));
	}
}
