using Microsoft.Maui.Controls.Hosting;
using Microsoft.Maui.Hosting;

namespace AndroidViewHandlerBackgroundImageRetentionRepro;

public static class MauiProgram
{
	public static MauiApp CreateMauiApp()
	{
		var builder = MauiApp.CreateBuilder();

		builder
			.UseMauiApp<App>()
			.ConfigureImageSources(services =>
			{
				services.AddService<PayloadImageSource, PayloadImageSourceService>();
			});

		return builder.Build();
	}
}
