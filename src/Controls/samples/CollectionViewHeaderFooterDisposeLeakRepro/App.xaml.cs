#nullable enable

using System;
using Microsoft.Maui;
using Microsoft.Maui.Controls;

namespace CollectionViewHeaderFooterDisposeLeakRepro;

public partial class App : Application
{
	readonly string? _resultsPath;

	public App()
	{
		InitializeComponent();

		_resultsPath = Environment.GetEnvironmentVariable("REPRO_RESULTS_PATH");
	}

	protected override Window CreateWindow(IActivationState? activationState)
	{
		return new Window(new MainPage(_resultsPath));
	}
}
