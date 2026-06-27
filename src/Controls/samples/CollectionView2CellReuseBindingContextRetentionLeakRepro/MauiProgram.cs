using Microsoft.Maui.Controls.Hosting;
using Microsoft.Maui.Hosting;

namespace CollectionView2CellReuseBindingContextRetentionLeakRepro;

public static class MauiProgram
{
	public static MauiApp CreateMauiApp()
	{
		return MauiApp.CreateBuilder()
			.UseMauiApp<App>()
			.ConfigureMauiHandlers(handlers =>
			{
				handlers.AddHandler<PayloadItemView, PayloadItemViewHandler>();
			})
			.ConfigureFonts(fonts => { })
			.Build();
	}
}
