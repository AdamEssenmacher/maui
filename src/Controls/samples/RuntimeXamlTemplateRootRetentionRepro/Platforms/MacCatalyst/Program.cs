using ObjCRuntime;
using UIKit;

namespace RuntimeXamlTemplateRootRetentionRepro;

public class Program
{
	static void Main(string[] args) => UIApplication.Main(args, null, typeof(AppDelegate));
}
