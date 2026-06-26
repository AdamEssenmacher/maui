using Microsoft.Maui.Controls.Hosting;
using Microsoft.Maui.Hosting;

namespace RefreshViewChildHandlerDisconnectLeakRepro;

public static class MauiProgram
{
	public static MauiApp CreateMauiApp()
	{
		return MauiApp.CreateBuilder()
			.UseMauiApp<App>()
			.ConfigureMauiHandlers(handlers =>
			{
				handlers.AddHandler<Microsoft.Maui.Controls.RefreshView, Microsoft.Maui.Handlers.RefreshViewHandler>();
				handlers.AddHandler<PayloadRefreshContentView, PayloadRefreshContentViewHandler>();
			})
			.ConfigureFonts(fonts => { })
			.Build();
	}
}
