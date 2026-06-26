using Microsoft.Maui.Controls.Hosting;
using Microsoft.Maui.Hosting;

namespace SwipeItemViewChildHandlerDisconnectLeakRepro;

public static class MauiProgram
{
	public static MauiApp CreateMauiApp()
	{
		return MauiApp.CreateBuilder()
			.UseMauiApp<App>()
			.ConfigureMauiHandlers(handlers =>
			{
				handlers.AddHandler<Microsoft.Maui.Controls.SwipeItemView, Microsoft.Maui.Handlers.SwipeItemViewHandler>();
				handlers.AddHandler<PayloadSwipeContentView, PayloadSwipeContentViewHandler>();
			})
			.ConfigureFonts(fonts => { })
			.Build();
	}
}
