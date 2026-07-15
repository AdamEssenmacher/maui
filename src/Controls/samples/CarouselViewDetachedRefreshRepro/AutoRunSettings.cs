namespace CarouselViewDetachedRefreshRepro;

internal static class AutoRunSettings
{
	const string AutoRunEnvironmentVariable = "CAROUSEL_REPRO_AUTORUN";
	const string BuildLabelEnvironmentVariable = "CAROUSEL_REPRO_BUILD_LABEL";

	public static bool IsEnabled { get; private set; }
	public static string BuildLabel { get; private set; } = "interactive";

	public static void Initialize(string[] args)
	{
		IsEnabled = IsTrue(Environment.GetEnvironmentVariable(AutoRunEnvironmentVariable)) ||
			args.Any(argument => string.Equals(argument, "--auto-run", StringComparison.OrdinalIgnoreCase));

		BuildLabel = Environment.GetEnvironmentVariable(BuildLabelEnvironmentVariable) ?? "interactive";
	}

	static bool IsTrue(string? value) =>
		string.Equals(value, "1", StringComparison.OrdinalIgnoreCase) ||
		string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) ||
		string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase);
}
