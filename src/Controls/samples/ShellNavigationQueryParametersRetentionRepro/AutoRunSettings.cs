namespace ShellNavigationQueryParametersRetentionRepro;

internal static class AutoRunSettings
{
	const string ResultsFileName = "shell-queryparameters-retention-results.txt";

	public static string ResultsPath { get; private set; } = Path.Combine(Path.GetTempPath(), ResultsFileName);
	public static string Arguments { get; private set; } = string.Empty;

	public static void Initialize(string[] args)
	{
		Arguments = string.Join(" ", args);

		foreach (var arg in args)
		{
			const string resultsPrefix = "--results=";
			if (arg.StartsWith(resultsPrefix, StringComparison.OrdinalIgnoreCase))
				ResultsPath = arg[resultsPrefix.Length..];
		}

		ResetResultsFile();
		WriteStartupMarker("Program.Main args: " + Arguments);
		WriteStartupMarker("ResultsPath: " + ResultsPath);
	}

	public static void WriteStartupMarker(string message)
	{
		try
		{
			File.AppendAllText(ResultsPath, message + Environment.NewLine);
		}
		catch
		{
			// Startup markers must never hide the repro result.
		}
	}

	static void ResetResultsFile()
	{
		if (TryResetResultsFile())
			return;

		ResultsPath = Path.Combine(Path.GetTempPath(), ResultsFileName);
		TryResetResultsFile();
	}

	static bool TryResetResultsFile()
	{
		try
		{
			File.WriteAllText(ResultsPath, string.Empty);
			return true;
		}
		catch
		{
			return false;
		}
	}
}
