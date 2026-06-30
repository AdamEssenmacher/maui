#nullable enable

using Microsoft.Maui.Controls;

namespace AndroidCollectionViewHeaderFooterHolderRetentionRepro;

public partial class App : Application
{
	public App()
	{
		InitializeComponent();
		MainPage = new MainPage();
	}
}
