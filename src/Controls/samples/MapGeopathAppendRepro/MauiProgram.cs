using Microsoft.Maui.Controls.Hosting;
using Microsoft.Maui.Hosting;

namespace MapGeopathAppendRepro;

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
