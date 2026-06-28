using Microsoft.Maui.Controls.Hosting;
using Microsoft.Maui.Hosting;

namespace IosFlyoutPageLeftBarButtonAccessibilityRetentionRepro;

public static class MauiProgram
{
	public static MauiApp CreateMauiApp()
	{
		return MauiApp.CreateBuilder()
			.UseMauiApp<App>()
			.Build();
	}
}
