namespace PathDataSharedGeometryLeakRepro;

internal static class AutoRunSettings
{
	public const string TargetName = "PathDataSharedGeometry";

	public static bool Enabled { get; private set; }

	public static string? ResultsPath { get; private set; }

	public static string? Target { get; private set; }

	public static bool ShouldRunPathDataSharedGeometry =>
		Enabled &&
		(string.IsNullOrWhiteSpace(Target) || string.Equals(Target, TargetName, StringComparison.OrdinalIgnoreCase));

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
			{
				ResultsPath = arg[resultsPrefix.Length..];
				continue;
			}

			const string targetPrefix = "--target=";
			if (arg.StartsWith(targetPrefix, StringComparison.OrdinalIgnoreCase))
				Target = arg[targetPrefix.Length..];
		}

		if (string.Equals(Environment.GetEnvironmentVariable("PATH_DATA_SHARED_GEOMETRY_LEAK_REPRO_AUTORUN"), "1", StringComparison.Ordinal))
			Enabled = true;

		ResultsPath ??= Environment.GetEnvironmentVariable("PATH_DATA_SHARED_GEOMETRY_LEAK_REPRO_RESULTS");
		Target ??= Environment.GetEnvironmentVariable("PATH_DATA_SHARED_GEOMETRY_LEAK_REPRO_TARGET");
	}
}
