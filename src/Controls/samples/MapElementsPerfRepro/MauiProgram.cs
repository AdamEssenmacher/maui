using Microsoft.Maui.Controls.Hosting;
using Microsoft.Maui.Hosting;

namespace MapElementsPerfRepro;

public static class MauiProgram
{
	public static MauiApp CreateMauiApp()
	{
		AutoRunSettings.Initialize(Environment.GetCommandLineArgs());

		return MauiApp.CreateBuilder()
			.UseMauiApp<App>()
			.UseMauiMaps()
			.ConfigureFonts(fonts => { })
			.Build();
	}
}
