using Microsoft.Maui.Controls.Hosting;
using Microsoft.Maui.Hosting;

namespace SwipeViewChildHandlerDisconnectLeakRepro;

public static class MauiProgram
{
	public static MauiApp CreateMauiApp()
	{
		return MauiApp.CreateBuilder()
			.UseMauiApp<App>()
			.ConfigureMauiHandlers(handlers =>
			{
				handlers.AddHandler<Microsoft.Maui.Controls.SwipeView, Microsoft.Maui.Handlers.SwipeViewHandler>();
				handlers.AddHandler<PayloadSwipeContentView, PayloadSwipeContentViewHandler>();
			})
			.ConfigureFonts(fonts => { })
			.Build();
	}
}
