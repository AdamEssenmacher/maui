using Microsoft.Maui.Controls.Hosting;
using Microsoft.Maui.Hosting;

namespace ListViewHeaderDisconnectLeakRepro;

public static class MauiProgram
{
	public static MauiApp CreateMauiApp()
	{
		return MauiApp.CreateBuilder()
			.UseMauiApp<App>()
			.ConfigureMauiHandlers(handlers =>
			{
				handlers.AddHandler<PayloadHeaderView, PayloadHeaderViewHandler>();
			})
			.ConfigureFonts(fonts => { })
			.Build();
	}
}
