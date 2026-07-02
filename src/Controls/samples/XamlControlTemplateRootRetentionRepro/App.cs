using Microsoft.Maui.Controls;

namespace XamlControlTemplateRootRetentionRepro;

public sealed class App : Application
{
	const string DefaultResultsPath = "/tmp/xaml-controltemplate-root-retention-results.txt";

	protected override Window CreateWindow(IActivationState? activationState)
		=> new(new MainPage(GetResultsPath()));

	static string GetResultsPath()
	{
		foreach (var argument in Environment.GetCommandLineArgs())
		{
			const string Prefix = "--results=";
			if (argument.StartsWith(Prefix, StringComparison.Ordinal))
				return argument[Prefix.Length..];
		}

		return DefaultResultsPath;
	}
}
