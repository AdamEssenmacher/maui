using Microsoft.Maui.Controls.Compatibility.Hosting;
using Microsoft.Maui.Hosting;

namespace PhoneFlyoutPageBackgroundPatternRetentionRepro;

public static class MauiProgram
{
	public static MauiApp CreateMauiApp()
	{
		var builder = MauiApp.CreateBuilder();
		builder
			.UseMauiApp<App>()
			.UseMauiCompatibility()
			.ConfigureImageSources(services =>
			{
				services.AddService<TrackingImageSource, TrackingImageSourceService>();
			});

		return builder.Build();
	}
}
