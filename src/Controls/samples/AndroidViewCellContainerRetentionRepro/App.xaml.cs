#nullable enable

using Microsoft.Maui.Controls;

namespace AndroidViewCellContainerRetentionRepro;

public partial class App : Application
{
	public App()
	{
		InitializeComponent();
		MainPage = new MainPage();
	}
}
