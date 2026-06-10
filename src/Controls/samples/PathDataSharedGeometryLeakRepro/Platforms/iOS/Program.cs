using Foundation;
using UIKit;

namespace PathDataSharedGeometryLeakRepro;

public static class Program
{
	static void Main(string[] args)
	{
		AutoRunSettings.Initialize(GetStartupArguments(args));
		UIApplication.Main(args, null, typeof(AppDelegate));
	}

	static string[] GetStartupArguments(string[] args)
	{
		var processArguments = NSProcessInfo.ProcessInfo.Arguments;

		if (processArguments is null || processArguments.Length == 0)
			return args;

		return args
			.Concat(processArguments.Select(argument => argument?.ToString() ?? string.Empty))
			.Where(argument => !string.IsNullOrWhiteSpace(argument))
			.Distinct(StringComparer.Ordinal)
			.ToArray();
	}
}
