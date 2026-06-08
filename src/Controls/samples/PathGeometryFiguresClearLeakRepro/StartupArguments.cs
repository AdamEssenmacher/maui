namespace PathGeometryFiguresClearLeakRepro;

internal static class StartupArguments
{
	public static string[] Args { get; private set; } = Array.Empty<string>();

	public static void Set(string[] args)
	{
		Args = args;
	}
}
