using Microsoft.Maui;
using Microsoft.Maui.Controls;

namespace AndroidShellFlyoutNativeHookLeakRepro;

public sealed class App : Application
{
	protected override Window CreateWindow(IActivationState? activationState)
	{
		return new Window(new ReproShell());
	}
}

sealed class ReproShell : Shell
{
	public ReproShell()
	{
		Title = "Shell Flyout Native Hook Leak";

		Items.Add(new FlyoutItem
		{
			Title = "Leak Repro",
			Items =
			{
				new ShellContent
				{
					Title = "Run",
					Content = new ReproPage(this)
				}
			}
		});
	}
}
