using UIKit;

namespace IosCompatActionSheetObserverRetentionRepro;

public class Program
{
	static void Main(string[] args)
	{
		AutoRunSettings.Initialize(args);
		UIApplication.Main(args, null, typeof(AppDelegate));
	}
}
