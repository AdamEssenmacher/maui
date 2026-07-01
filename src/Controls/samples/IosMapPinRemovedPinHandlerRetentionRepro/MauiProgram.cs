using Microsoft.Maui.Controls.Hosting;
using Microsoft.Maui.Hosting;

namespace IosMapPinRemovedPinHandlerRetentionRepro;

public static class MauiProgram
{
	public static MauiApp CreateMauiApp()
	{
		return MauiApp.CreateBuilder()
			.UseMauiApp<App>()
			.UseMauiMaps()
			.Build();
	}
}
