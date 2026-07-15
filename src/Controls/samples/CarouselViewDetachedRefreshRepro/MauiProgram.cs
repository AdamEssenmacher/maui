using Microsoft.Maui.Controls.Handlers.Items2;
using Microsoft.Maui.Controls.Hosting;
using Microsoft.Maui.Hosting;

namespace CarouselViewDetachedRefreshRepro;

public static class MauiProgram
{
	public static MauiApp CreateMauiApp()
	{
		return MauiApp.CreateBuilder()
			.UseMauiApp<App>()
			.ConfigureMauiHandlers(handlers =>
			{
				// Register CV2 explicitly so this project always exercises the affected controller.
				handlers.AddHandler<CarouselView, CarouselViewHandler2>();
			})
			.ConfigureFonts(fonts => { })
			.Build();
	}
}
