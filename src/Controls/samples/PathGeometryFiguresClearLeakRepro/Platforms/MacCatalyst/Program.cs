using UIKit;

namespace PathGeometryFiguresClearLeakRepro;

public static class Program
{
	static void Main(string[] args)
	{
		StartupArguments.Set(args);
		UIApplication.Main(args, null, typeof(AppDelegate));
	}
}
