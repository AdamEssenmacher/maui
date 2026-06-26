using Microsoft.Maui.Controls.Hosting;
using Microsoft.Maui.Hosting;

namespace ContentViewChildHandlerDisconnectLeakRepro;

public static class MauiProgram
{
	public static MauiApp CreateMauiApp()
	{
		return MauiApp.CreateBuilder()
			.UseMauiApp<App>()
			.ConfigureMauiHandlers(handlers =>
			{
				handlers.AddHandler<PayloadContentView, PayloadContentViewHandler>();
			})
			.ConfigureFonts(fonts => { })
			.Build();
	}
}
