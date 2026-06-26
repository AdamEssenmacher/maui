using Microsoft.Maui;
using Microsoft.Maui.Controls;

namespace AndroidDragDropLocalStateRetentionLeakRepro;

public sealed class App : Application
{
	protected override Window CreateWindow(IActivationState activationState)
	{
		return new Window(new MainPage());
	}
}
