#nullable enable

using Microsoft.Maui.Controls;

namespace AndroidListViewCellTextRetentionRepro;

public partial class App : Application
{
	public App()
	{
		InitializeComponent();
		MainPage = new MainPage();
	}
}
