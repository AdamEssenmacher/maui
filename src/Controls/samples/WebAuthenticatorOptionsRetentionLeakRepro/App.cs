namespace WebAuthenticatorOptionsRetentionLeakRepro;

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
		var text = report.ToText();
		var path = AutoRunSettings.GetResultsPath();
		Directory.CreateDirectory(Path.GetDirectoryName(path)!);
		File.WriteAllText(path, text);
#if ANDROID
		Android.Util.Log.Info("WebAuthenticatorOptionsRetentionLeakRepro", text);
		Android.OS.Process.KillProcess(Android.OS.Process.MyPid());
#else
		Environment.Exit(report.LeakProved ? 0 : 2);
#endif
	}
}
