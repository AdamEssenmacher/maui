using Microsoft.Maui.Hosting;

namespace ActivityStateManagerLeakRepro;

public static class MauiProgram
{
	public static MauiApp CreateMauiApp()
	{
		AutoRunSettings.Initialize([]);

		return MauiApp
			.CreateBuilder()
			.UseMauiApp<App>()
			.Build();
	}
}
