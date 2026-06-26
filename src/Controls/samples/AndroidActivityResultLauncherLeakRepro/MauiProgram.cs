using Microsoft.Maui.Hosting;

namespace AndroidActivityResultLauncherLeakRepro;

public static class MauiProgram
{
	public static MauiApp CreateMauiApp() =>
		MauiApp
			.CreateBuilder()
			.UseMauiApp<App>()
			.Build();
}
