namespace ActivityStateManagerLeakRepro;

internal static class AutoRunSettings
{
	public static bool Enabled { get; private set; }

	public static string? ResultsPath { get; private set; }

	public static void Initialize(string[] args)
	{
#if ACTIVITY_STATE_MANAGER_LEAK_REPRO_AUTORUN
		Enabled = true;
#endif

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

		if (string.Equals(Environment.GetEnvironmentVariable("ACTIVITY_STATE_MANAGER_LEAK_REPRO_AUTORUN"), "1", StringComparison.Ordinal))
			Enabled = true;

		ResultsPath ??= Environment.GetEnvironmentVariable("ACTIVITY_STATE_MANAGER_LEAK_REPRO_RESULTS");
	}
}
