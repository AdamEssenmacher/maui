using Microsoft.Maui.Hosting;

namespace Maui.Controls.HybridWebViewRepro;

public static class MauiProgram
{
	public static MauiApp CreateMauiApp()
	{
		var builder = MauiApp.CreateBuilder()
			.UseMauiApp<App>();

#if DEBUG
		builder.Services.AddHybridWebViewDeveloperTools();
#endif

		return builder.Build();
	}
}
