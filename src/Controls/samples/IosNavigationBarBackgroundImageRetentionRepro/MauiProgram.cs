using Microsoft.Maui.Controls.Hosting;
using Microsoft.Maui.Hosting;

namespace IosNavigationBarBackgroundImageRetentionRepro;

public static class MauiProgram
{
	public static MauiApp CreateMauiApp()
	{
		return MauiApp.CreateBuilder()
			.UseMauiApp<App>()
			.Build();
	}
}
