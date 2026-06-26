using Microsoft.Maui.Controls.Hosting;
using Microsoft.Maui.Hosting;

namespace BorderChildHandlerDisconnectLeakRepro;

public static class MauiProgram
{
	public static MauiApp CreateMauiApp()
	{
		return MauiApp.CreateBuilder()
			.UseMauiApp<App>()
			.ConfigureMauiHandlers(handlers =>
			{
				handlers.AddHandler<Microsoft.Maui.Controls.Border, Microsoft.Maui.Handlers.BorderHandler>();
				handlers.AddHandler<PayloadBorderContentView, PayloadBorderContentViewHandler>();
			})
			.ConfigureFonts(fonts => { })
			.Build();
	}
}
