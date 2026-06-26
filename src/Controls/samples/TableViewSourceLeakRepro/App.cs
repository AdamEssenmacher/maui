namespace TableViewSourceLeakRepro;

public sealed class App : Application
{
	public App()
	{
		AutoRunSettings.Initialize(Environment.GetCommandLineArgs());

		if (AutoRunSettings.Enabled)
			RunAndExit();
	}

	protected override Window CreateWindow(IActivationState? activationState)
	{
		return new Window(new ReproPage());
	}

	static void RunAndExit()
	{
		var report = ReproSession.Run();
		var path = AutoRunSettings.GetResultsPath();
		Directory.CreateDirectory(Path.GetDirectoryName(path)!);
		File.WriteAllText(path, report.ToText());
		Environment.Exit(report.LeakProved ? 0 : 2);
	}
}
