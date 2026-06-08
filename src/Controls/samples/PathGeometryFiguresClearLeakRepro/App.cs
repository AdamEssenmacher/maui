namespace PathGeometryFiguresClearLeakRepro;

public sealed class App : Application
{
	readonly string[] _args;

	public App()
	{
		_args = GetStartupArguments();
	}

	protected override Window CreateWindow(IActivationState? activationState)
	{
		return new Window(new NavigationPage(new MainPage(_args)));
	}

	static string[] GetStartupArguments()
	{
		var args = new List<string>();
		args.AddRange(StartupArguments.Args);
		args.AddRange(Environment.GetCommandLineArgs());

#if IOS || MACCATALYST
		args.AddRange(Foundation.NSProcessInfo.ProcessInfo.Arguments);
#endif

		return args
			.Where(arg => !string.IsNullOrWhiteSpace(arg))
			.Distinct(StringComparer.OrdinalIgnoreCase)
			.ToArray();
	}
}
