#nullable enable

using Microsoft.Maui.Controls;

namespace TableViewCellParentRetentionLeakRepro;

public sealed class App : Application
{
	public App()
	{
		MainPage = new MainPage();
	}
}
