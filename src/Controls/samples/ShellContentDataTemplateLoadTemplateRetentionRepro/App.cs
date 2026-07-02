using Microsoft.Maui;
using Microsoft.Maui.Controls;

namespace ShellContentDataTemplateLoadTemplateRetentionRepro;

public sealed class App : Application
{
	protected override Window CreateWindow(IActivationState? activationState)
	{
		return new Window(new MainPage(GetResultsPath()));
	}

	static string? GetResultsPath()
	{
		var args = Environment.GetCommandLineArgs();
		for (var i = 0; i < args.Length; i++)
		{
			if (args[i].StartsWith("--results=", StringComparison.Ordinal))
				return args[i]["--results=".Length..];

			if (args[i] == "--results" && i + 1 < args.Length)
				return args[i + 1];
		}

		return null;
	}
}
