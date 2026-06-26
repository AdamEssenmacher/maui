namespace StreamImageSourceUndisposedStreamLeakRepro;

static class CommandLineOptions
{
	const string ResultsPrefix = "--results=";

	public static string GetResultsPath()
	{
		foreach (var arg in Environment.GetCommandLineArgs())
		{
			if (arg.StartsWith(ResultsPrefix, StringComparison.Ordinal))
				return arg[ResultsPrefix.Length..];
		}

		return Path.Combine(
			Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
			"StreamImageSourceUndisposedStreamLeakRepro",
			"autorun-results.txt");
	}
}
