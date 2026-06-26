#nullable enable

using System;
using System.Linq;
using Microsoft.Maui;
using Microsoft.Maui.Controls;

namespace HybridWebViewFailedInvokeCallbackLeakRepro;

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

		return new Window(new MainPage(resultsPath));
	}
}
