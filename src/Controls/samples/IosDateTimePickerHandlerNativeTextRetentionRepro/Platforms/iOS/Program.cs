using UIKit;

namespace IosDateTimePickerHandlerNativeTextRetentionRepro;

public static class Program
{
	static void Main(string[] args)
	{
		AutoRunSettings.Initialize(args);
		UIApplication.Main(args, null, typeof(AppDelegate));
	}
}
