using Microsoft.Maui.Controls.Hosting;
using Microsoft.Maui.Hosting;

namespace LayoutChildHandlerDisconnectLeakRepro;

public static class MauiProgram
{
	public static MauiApp CreateMauiApp()
	{
		return MauiApp.CreateBuilder()
			.UseMauiApp<App>()
			.ConfigureMauiHandlers(handlers =>
			{
				handlers.AddHandler<PayloadLayoutChildView, PayloadLayoutChildViewHandler>();
			})
			.ConfigureFonts(fonts => { })
			.Build();
	}
}
