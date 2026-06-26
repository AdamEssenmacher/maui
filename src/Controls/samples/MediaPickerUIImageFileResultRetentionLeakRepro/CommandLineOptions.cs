namespace MediaPickerUIImageFileResultRetentionLeakRepro;

static class CommandLineOptions
{
	public static string GetResultsPath()
	{
		foreach (var arg in Environment.GetCommandLineArgs())
		{
			const string Prefix = "--results=";
			if (arg.StartsWith(Prefix, StringComparison.Ordinal))
				return arg[Prefix.Length..];
		}

		return Path.Combine(Path.GetTempPath(), "mediapickeruiimagefileresultretentionleakrepro-results.txt");
	}
}
