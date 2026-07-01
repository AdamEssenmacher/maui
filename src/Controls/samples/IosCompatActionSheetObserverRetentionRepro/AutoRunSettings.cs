namespace IosCompatActionSheetObserverRetentionRepro;

internal static class AutoRunSettings
{
	const string AutoRunArgument = "--auto-run";
	const string ResultsPathArgument = "--results-path=";

	static string _resultsPath = Path.Combine(
		Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
		"ios-compat-actionsheet-observer-retention-results.txt");

	public static bool Enabled { get; private set; }

	public static void Initialize(string[] arguments)
	{
		foreach (var argument in arguments)
		{
			if (string.Equals(argument, AutoRunArgument, StringComparison.Ordinal))
			{
				Enabled = true;
			}
			else if (argument.StartsWith(ResultsPathArgument, StringComparison.Ordinal))
			{
				var path = argument[ResultsPathArgument.Length..];
				if (!string.IsNullOrWhiteSpace(path))
					_resultsPath = path;
			}
		}
	}

	public static string GetResultsPath() => _resultsPath;
}
