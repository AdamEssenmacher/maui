namespace PageInternalChildrenClearRetentionRepro;

public sealed class AutoRunSettings
{
	public string ResultsPath { get; set; } =
		Path.Combine(Path.GetTempPath(), "page-internalchildren-clear-retention-results.txt");

	public static AutoRunSettings FromArgs(string[] args)
	{
		var settings = new AutoRunSettings();

		foreach (var arg in args)
		{
			const string resultsPrefix = "--results=";
			if (arg.StartsWith(resultsPrefix, StringComparison.Ordinal))
				settings.ResultsPath = arg[resultsPrefix.Length..];
		}

		return settings;
	}
}
