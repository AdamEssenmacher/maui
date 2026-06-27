namespace PHPickerProcessedOriginalRetentionLeakRepro;

static class CommandLineOptions
{
	public static string? ResultsPath { get; } = GetOptionValue("--results");

	static string? GetOptionValue(string name)
	{
		foreach (var arg in Environment.GetCommandLineArgs())
		{
			if (arg.StartsWith(name + "=", StringComparison.Ordinal))
				return arg[(name.Length + 1)..];
		}

		return null;
	}
}
