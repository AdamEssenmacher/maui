using Microsoft.Maui.Storage;

namespace MapElementsPerfRepro;

internal static class AutoRunSettings
{
	static readonly object s_fileLock = new();

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

		if (string.Equals(Environment.GetEnvironmentVariable("MAP_ELEMENTS_PERF_REPRO_AUTORUN"), "1", StringComparison.Ordinal))
			Enabled = true;

		ResultsPath ??= Environment.GetEnvironmentVariable("MAP_ELEMENTS_PERF_REPRO_RESULTS");
	}

	public static void ResetResultsFile()
	{
		var path = GetResultsPath();
		var directory = Path.GetDirectoryName(path);

		if (!string.IsNullOrEmpty(directory))
			Directory.CreateDirectory(directory);

		lock (s_fileLock)
		{
			if (File.Exists(path))
				File.Delete(path);
		}
	}

	public static void AppendResult(ReproResult result)
	{
		var path = GetResultsPath();
		var directory = Path.GetDirectoryName(path);

		if (!string.IsNullOrEmpty(directory))
			Directory.CreateDirectory(directory);

		lock (s_fileLock)
		{
			File.AppendAllText(path, result.ToOutput());
			File.AppendAllText(path, $"{Environment.NewLine}---{Environment.NewLine}");
		}
	}

	public static string GetResultsPath()
	{
		return string.IsNullOrWhiteSpace(ResultsPath)
			? Path.Combine(FileSystem.AppDataDirectory, "autorun-results.txt")
			: ResultsPath;
	}
}
