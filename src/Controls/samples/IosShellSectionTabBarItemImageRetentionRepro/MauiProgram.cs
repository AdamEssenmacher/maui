using Microsoft.Maui.Controls.Hosting;
using Microsoft.Maui.Hosting;

namespace IosShellSectionTabBarItemImageRetentionRepro;

public static class MauiProgram
{
	public static MauiApp CreateMauiApp()
	{
		return MauiApp.CreateBuilder()
			.UseMauiApp<App>()
			.ConfigureImageSources(services =>
			{
				services.AddService<TrackingImageSource>(_ => new TrackingImageSourceService());
			})
			.Build();
	}
}
