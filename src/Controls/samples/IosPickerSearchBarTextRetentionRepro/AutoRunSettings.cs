namespace IosPickerSearchBarTextRetentionRepro;

internal static class AutoRunSettings
{
	public static bool Enabled { get; private set; }

	public static string? ResultsPath { get; private set; }

	public static string GetResultsPath()
	{
		return ResultsPath ?? Path.Combine(Path.GetTempPath(), "ios-picker-searchbar-text-retention-results.txt");
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

		if (string.Equals(Environment.GetEnvironmentVariable("IOS_PICKER_SEARCHBAR_TEXT_RETENTION_REPRO_AUTORUN"), "1", StringComparison.Ordinal))
			Enabled = true;

		ResultsPath ??= Environment.GetEnvironmentVariable("IOS_PICKER_SEARCHBAR_TEXT_RETENTION_REPRO_RESULTS");
	}
}
