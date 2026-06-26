namespace ListViewCleanupHeaderFooterLeakRepro;

internal static class AutoRunSettings
{
	public static bool Enabled { get; private set; }

	public static string? ResultsPath { get; private set; }

	public static string GetResultsPath()
	{
		return ResultsPath ?? Path.Combine(Path.GetTempPath(), "listviewcleanupheaderfooterleakrepro-results.txt");
	}

	public static void Initialize(string[] args)
	{
		foreach (var arg in args)
		{
			if (string.Equals(arg, "--auto-run", StringComparison.OrdinalIgnoreCase))
			{
				Enabled = true;
				continue;
			}

			const string resultsPrefix = "--results=";
			if (arg.StartsWith(resultsPrefix, StringComparison.OrdinalIgnoreCase))
				ResultsPath = arg[resultsPrefix.Length..];
		}

		if (string.Equals(Environment.GetEnvironmentVariable("LISTVIEW_CLEANUP_HEADER_FOOTER_LEAK_REPRO_AUTORUN"), "1", StringComparison.Ordinal))
			Enabled = true;

		ResultsPath ??= Environment.GetEnvironmentVariable("LISTVIEW_CLEANUP_HEADER_FOOTER_LEAK_REPRO_RESULTS");
	}
}
