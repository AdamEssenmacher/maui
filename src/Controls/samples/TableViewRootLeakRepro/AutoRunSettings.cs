namespace TableViewRootLeakRepro;

internal static class AutoRunSettings
{
	public static bool Enabled { get; private set; }
	public static string? ResultsPath { get; private set; }

	public static void Initialize(string[] args)
	{
		foreach (var arg in args)
		{
			if (string.Equals(arg, "--auto-run", StringComparison.OrdinalIgnoreCase))
				Enabled = true;

			const string resultsPrefix = "--results=";
			if (arg.StartsWith(resultsPrefix, StringComparison.OrdinalIgnoreCase))
				ResultsPath = arg[resultsPrefix.Length..];
		}

		if (Environment.GetEnvironmentVariable("TABLEVIEW_ROOT_LEAK_REPRO_AUTORUN") == "1")
			Enabled = true;

		ResultsPath ??= Environment.GetEnvironmentVariable("TABLEVIEW_ROOT_LEAK_REPRO_RESULTS");
	}
}
