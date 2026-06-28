using Microsoft.Maui.Controls.Hosting;
using Microsoft.Maui.Hosting;

namespace IndicatorViewTemplateDisconnectRetentionRepro;

public static class MauiProgram
{
	public static MauiApp CreateMauiApp()
	{
		var builder = MauiApp.CreateBuilder();
		builder
			.UseMauiApp<App>()
			.ConfigureMauiHandlers(handlers =>
			{
				handlers.AddHandler<PayloadIndicatorView, PayloadIndicatorViewHandler>();
			});

		return builder.Build();
	}
}
