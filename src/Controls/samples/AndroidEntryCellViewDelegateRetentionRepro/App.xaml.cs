#nullable enable

using Microsoft.Maui.Controls;

namespace AndroidEntryCellViewDelegateRetentionRepro;

public partial class App : Application
{
	public App()
	{
		InitializeComponent();
		MainPage = new MainPage();
	}
}
