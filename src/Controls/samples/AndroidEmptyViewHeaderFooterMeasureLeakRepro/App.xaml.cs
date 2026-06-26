#nullable enable

using System;
using System.IO;
using System.Linq;
using Microsoft.Maui;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Storage;

namespace AndroidEmptyViewHeaderFooterMeasureLeakRepro;

public partial class App : Application
{
	public App()
	{
		InitializeComponent();
	}

	protected override Window CreateWindow(IActivationState? activationState)
	{
		var args = Environment.GetCommandLineArgs();
		var resultsPath = args
			.FirstOrDefault(arg => arg.StartsWith("--results=", StringComparison.OrdinalIgnoreCase))
			?.Substring("--results=".Length);

		resultsPath ??= Path.Combine(FileSystem.AppDataDirectory, "autorun-results.txt");

		return new Window(new MainPage(resultsPath));
	}
}
