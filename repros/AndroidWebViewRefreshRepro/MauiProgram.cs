using Microsoft.Extensions.Logging;
using Microsoft.Maui.Hosting;

namespace AndroidWebViewRefreshRepro;

public static class MauiProgram
{
	public static MauiApp CreateMauiApp()
	{
		var builder = MauiApp.CreateBuilder();
		builder
			.UseMauiApp<App>()
			.ConfigureFonts(fonts =>
			{
			});

		builder.Logging.AddDebug();

		return builder.Build();
	}
}
