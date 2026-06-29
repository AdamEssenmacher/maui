#nullable enable

using Microsoft.Maui.Controls;

namespace AndroidImageCellNativeImageRetentionRepro;

public partial class App : Application
{
	public App()
	{
		InitializeComponent();
		MainPage = new MainPage();
	}
}
