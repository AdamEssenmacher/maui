#nullable enable

using Microsoft.Maui.Controls;

namespace AndroidSwitchCellNativeTextRetentionRepro;

public partial class App : Application
{
	public App()
	{
		InitializeComponent();
		MainPage = new MainPage();
	}
}
