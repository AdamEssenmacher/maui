#nullable enable

using Microsoft.Maui.Controls;

namespace AndroidEntryCellNativeTextRetentionRepro;

public partial class App : Application
{
	public App()
	{
		InitializeComponent();
		MainPage = new MainPage();
	}
}
