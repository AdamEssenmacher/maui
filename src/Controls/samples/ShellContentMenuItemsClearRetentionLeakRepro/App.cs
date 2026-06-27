#nullable enable

using Microsoft.Maui.Controls;

namespace ShellContentMenuItemsClearRetentionLeakRepro;

public sealed class App : Application
{
	public App()
	{
		MainPage = new MainPage();
	}
}
