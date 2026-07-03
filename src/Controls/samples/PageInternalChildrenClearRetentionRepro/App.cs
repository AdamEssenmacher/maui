using Microsoft.Maui.Controls;

namespace PageInternalChildrenClearRetentionRepro;

public sealed class App : Application
{
	protected override Window CreateWindow(IActivationState? activationState)
	{
		var settings = AutoRunSettings.FromArgs(Environment.GetCommandLineArgs());
		var result = PageInternalChildrenClearRetentionProbe.Run();
		var report = result.ToReport();

		var resultsDirectory = Path.GetDirectoryName(settings.ResultsPath);
		if (!string.IsNullOrEmpty(resultsDirectory))
			Directory.CreateDirectory(resultsDirectory);

		File.WriteAllText(settings.ResultsPath, report);
		Console.WriteLine(report);

		Environment.Exit(result.Proven ? 0 : 2);

		return new Window(new ContentPage());
	}
}
