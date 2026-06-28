using Microsoft.Maui.Controls.Hosting;
using Microsoft.Maui.Hosting;

namespace IosSecondaryToolbarActionImageRetentionRepro;

public static class MauiProgram
{
	public static MauiApp CreateMauiApp()
	{
		return MauiApp.CreateBuilder()
			.UseMauiApp<App>()
			.ConfigureImageSources(services =>
			{
				services.AddService<TrackingFileImageSource>(_ => new TrackingImageSourceService());
			})
			.Build();
	}
}
