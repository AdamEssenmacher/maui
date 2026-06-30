#nullable enable

using Microsoft.Maui.Controls;

namespace AndroidSimpleViewHolderTextRetentionRepro;

public partial class App : Application
{
	public App()
	{
		InitializeComponent();
		MainPage = new MainPage();
	}
}
