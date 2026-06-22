namespace IndicatorViewTemplateSwapLeakRepro;

internal static class AutoRunSettings
{
	public static bool Enabled { get; private set; }

	public static string? ResultsPath { get; private set; }

	public static void Enable(string? resultsPath = null)
	{
		Enabled = true;

		if (!string.IsNullOrWhiteSpace(resultsPath))
			ResultsPath = resultsPath;
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

		if (string.Equals(Environment.GetEnvironmentVariable("INDICATOR_TEMPLATE_SWAP_LEAK_REPRO_AUTORUN"), "1", StringComparison.Ordinal))
			Enabled = true;

		ResultsPath ??= Environment.GetEnvironmentVariable("INDICATOR_TEMPLATE_SWAP_LEAK_REPRO_RESULTS");
	}
}
