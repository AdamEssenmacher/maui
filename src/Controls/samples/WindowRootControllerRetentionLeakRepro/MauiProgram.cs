using Microsoft.Maui.Controls.Hosting;
using Microsoft.Maui.Hosting;

namespace WindowRootControllerRetentionLeakRepro;

public static class MauiProgram
{
	public static MauiApp CreateMauiApp() =>
		MauiApp
			.CreateBuilder()
			.UseMauiApp<App>()
			.Build();
}
