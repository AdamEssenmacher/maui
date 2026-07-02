using Microsoft.Maui.Controls;

namespace SourceGenTemplateStaticResourceRetentionRepro;

public sealed class App : Application
{
	const string DefaultResultsPath = "/tmp/sourcegen-template-staticresource-retention-results.txt";

	protected override Window CreateWindow(IActivationState? activationState)
		=> new(new MainPage(GetResultsPath()));

	static string? GetResultsPath()
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
