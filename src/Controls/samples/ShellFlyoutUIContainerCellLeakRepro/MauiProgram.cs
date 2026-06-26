using Microsoft.Maui.Controls.Hosting;
using Microsoft.Maui.Hosting;

namespace ShellFlyoutUIContainerCellLeakRepro;

public static class MauiProgram
{
	public static MauiApp CreateMauiApp()
	{
		return MauiApp.CreateBuilder()
			.UseMauiApp<App>()
			.ConfigureMauiHandlers(handlers =>
			{
				handlers.AddHandler<PayloadFlyoutView, PayloadFlyoutViewHandler>();
			})
			.ConfigureFonts(fonts => { })
			.Build();
	}
}
