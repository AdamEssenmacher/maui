#if IOS || MACCATALYST
using Foundation;
#endif

namespace ShapeCollectionResetLeakRepro;

internal static class AutoRunSettings
{
	public static bool Enabled { get; private set; }

	public static string? ResultsPath { get; private set; }

	public static LeakTarget Target { get; private set; } = LeakTarget.PathFigureSegments;

	public static void Enable(string? resultsPath = null)
	{
		Enabled = true;

		if (!string.IsNullOrWhiteSpace(resultsPath))
			ResultsPath = resultsPath;
	}

	public static void Initialize(string[] args)
	{
		foreach (var arg in GetLaunchArguments(args))
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
			if (arg.StartsWith(targetPrefix, StringComparison.OrdinalIgnoreCase) &&
				TryParseTarget(arg[targetPrefix.Length..], out var target))
			{
				Target = target;
			}
		}

		if (string.Equals(Environment.GetEnvironmentVariable("SHAPE_COLLECTION_RESET_LEAK_REPRO_AUTORUN"), "1", StringComparison.Ordinal))
			Enabled = true;

		ResultsPath ??= Environment.GetEnvironmentVariable("SHAPE_COLLECTION_RESET_LEAK_REPRO_RESULTS");

		if (TryParseTarget(Environment.GetEnvironmentVariable("SHAPE_COLLECTION_RESET_LEAK_REPRO_TARGET"), out var environmentTarget))
			Target = environmentTarget;
	}

	static bool TryParseTarget(string? value, out LeakTarget target)
	{
		if (Enum.TryParse(value, ignoreCase: true, out target))
			return true;

		var normalized = value?
			.Replace(".", string.Empty, StringComparison.Ordinal)
			.Replace("_", string.Empty, StringComparison.Ordinal)
			.Replace("-", string.Empty, StringComparison.Ordinal)
			.Replace(" ", string.Empty, StringComparison.Ordinal)
			.ToLowerInvariant();

		target = normalized switch
		{
			"pathfiguresegments" or "segments" => LeakTarget.PathFigureSegments,
			"pathgeometryfigures" or "figures" => LeakTarget.PathGeometryFigures,
			"geometrygroupchildrenknownissue" or "geometrygroupchildren" or "geometrygroup" or "children" => LeakTarget.GeometryGroupChildrenKnownIssue,
			_ => default
		};

		return normalized is "pathfiguresegments" or "segments"
			or "pathgeometryfigures" or "figures"
			or "geometrygroupchildrenknownissue" or "geometrygroupchildren" or "geometrygroup" or "children";
	}

	static IEnumerable<string> GetLaunchArguments(string[] args)
	{
		foreach (var arg in args)
			yield return arg;

#if IOS || MACCATALYST
		foreach (var arg in NSProcessInfo.ProcessInfo.Arguments)
		{
			if (!string.IsNullOrWhiteSpace(arg))
				yield return arg;
		}
#endif
	}
}
