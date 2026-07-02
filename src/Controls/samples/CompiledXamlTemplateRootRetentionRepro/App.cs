using Microsoft.Maui.Controls;

namespace CompiledXamlTemplateRootRetentionRepro;

public sealed class App : Application
{
	const string DefaultResultsPath = "/tmp/compiled-xaml-template-root-retention-results.txt";

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
