using Microsoft.Maui.Controls.Hosting;
using Microsoft.Maui.Hosting;

namespace AndroidImageHandlerNativeDrawableRetentionRepro;

public static class MauiProgram
{
	public static MauiApp CreateMauiApp()
	{
		var builder = MauiApp.CreateBuilder();
		builder
			.UseMauiApp<App>()
			.ConfigureImageSources(services =>
			{
				services.AddService<TrackingImageSource>(_ => new TrackingImageSourceService());
			});

		return builder.Build();
	}
}
