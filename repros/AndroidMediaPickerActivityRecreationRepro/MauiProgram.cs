using Microsoft.Extensions.Logging;
using Microsoft.Maui.Hosting;

namespace AndroidMediaPickerActivityRecreationRepro;

public static class MauiProgram
{
	public static MauiApp CreateMauiApp()
	{
		var builder = MauiApp.CreateBuilder();
		builder.UseMauiApp<App>();
		builder.Logging.AddDebug();

		return builder.Build();
	}
}
