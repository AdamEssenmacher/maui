#nullable enable

using Microsoft.Maui.Controls;

namespace AndroidSwitchCellViewCellRetentionRepro;

public partial class App : Application
{
	public App()
	{
		InitializeComponent();
		MainPage = new MainPage();
	}
}
