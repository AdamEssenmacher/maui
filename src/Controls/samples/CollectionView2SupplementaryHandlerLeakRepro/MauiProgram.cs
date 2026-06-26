using Microsoft.Maui.Controls.Hosting;
using Microsoft.Maui.Hosting;

namespace CollectionView2SupplementaryHandlerLeakRepro;

public static class MauiProgram
{
	public static MauiApp CreateMauiApp()
	{
		return MauiApp.CreateBuilder()
			.UseMauiApp<App>()
			.ConfigureMauiHandlers(handlers =>
			{
				handlers.AddHandler<PayloadSupplementaryView, PayloadSupplementaryViewHandler>();
			})
			.ConfigureFonts(fonts => { })
			.Build();
	}
}
