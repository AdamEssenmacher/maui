using Microsoft.Maui.Controls.Hosting;
using Microsoft.Maui.Hosting;

namespace ShellFlyoutHeaderDisconnectLeakRepro;

public static class MauiProgram
{
	public static MauiApp CreateMauiApp()
	{
		return MauiApp.CreateBuilder()
			.UseMauiApp<App>()
			.ConfigureMauiHandlers(handlers =>
			{
				handlers.AddHandler<PayloadFlyoutHeaderView, PayloadFlyoutHeaderViewHandler>();
			})
			.ConfigureFonts(fonts => { })
			.Build();
	}
}
