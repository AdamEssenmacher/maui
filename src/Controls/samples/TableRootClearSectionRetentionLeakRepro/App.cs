#nullable enable

using Microsoft.Maui.Controls;

namespace TableRootClearSectionRetentionLeakRepro;

public sealed class App : Application
{
	public App()
	{
		MainPage = new MainPage();
	}
}
