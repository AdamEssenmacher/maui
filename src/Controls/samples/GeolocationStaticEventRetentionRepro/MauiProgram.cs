using Microsoft.Maui;
using Microsoft.Maui.Controls.Hosting;
using Microsoft.Maui.Hosting;

namespace GeolocationStaticEventRetentionRepro;

public static class MauiProgram
{
	public static MauiApp CreateMauiApp()
	{
		return MauiApp
			.CreateBuilder()
			.UseMauiApp<App>()
			.Build();
	}
}
