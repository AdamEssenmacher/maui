namespace WebAuthenticatorOptionsRetentionLeakRepro;

internal static class AutoRunSettings
{
	public static bool Enabled { get; private set; }

	public static string? ResultsPath { get; private set; }

	public static string GetResultsPath()
	{
#if ANDROID
		return ResultsPath ?? Path.Combine(Microsoft.Maui.Storage.FileSystem.AppDataDirectory, "autorun-results.txt");
#else
		return ResultsPath ?? Path.Combine(Path.GetTempPath(), "webauthenticatoroptionsretentionleakrepro-results.txt");
#endif
	}

	public static void Initialize(string[] args)
	{
#if ANDROID
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

		if (string.Equals(Environment.GetEnvironmentVariable("WEB_AUTHENTICATOR_OPTIONS_RETENTION_LEAK_REPRO_AUTORUN"), "1", StringComparison.Ordinal))
			Enabled = true;

		ResultsPath ??= Environment.GetEnvironmentVariable("WEB_AUTHENTICATOR_OPTIONS_RETENTION_LEAK_REPRO_RESULTS");
	}
}
