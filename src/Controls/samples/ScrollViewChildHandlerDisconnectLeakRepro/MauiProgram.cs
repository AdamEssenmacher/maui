using Microsoft.Maui.Controls.Hosting;
using Microsoft.Maui.Hosting;

namespace ScrollViewChildHandlerDisconnectLeakRepro;

public static class MauiProgram
{
	public static MauiApp CreateMauiApp()
	{
		return MauiApp.CreateBuilder()
			.UseMauiApp<App>()
			.ConfigureMauiHandlers(handlers =>
			{
				handlers.AddHandler<Microsoft.Maui.Controls.ScrollView, Microsoft.Maui.Handlers.ScrollViewHandler>();
				handlers.AddHandler<PayloadScrollContentView, PayloadScrollContentViewHandler>();
			})
			.ConfigureFonts(fonts => { })
			.Build();
	}
}
