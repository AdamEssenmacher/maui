using System.Diagnostics;

namespace SelectedItemsLeakRepro;

internal static class MetricsLogger
{
	public const string Tag = "SelectedItemsLeakReproMetrics";

	public static void Write(string message)
	{
		var line = $"{Tag}: {message}";

		Console.WriteLine(line);
		Debug.WriteLine(line);

#if ANDROID
		Android.Util.Log.Info(Tag, message);
#endif
	}

	public static void WriteBlock(string name, string value)
	{
		Write($"BEGIN {name}");

		foreach (var line in value.Split(Environment.NewLine, StringSplitOptions.None))
		{
			if (!string.IsNullOrWhiteSpace(line))
				Write(line);
		}

		Write($"END {name}");
	}
}
