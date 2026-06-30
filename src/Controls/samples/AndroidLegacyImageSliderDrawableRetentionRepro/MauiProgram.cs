using Microsoft.Maui.Controls.Hosting;
using Microsoft.Maui.Controls.Internals;
using Microsoft.Maui.Hosting;

namespace AndroidLegacyImageSliderDrawableRetentionRepro;

public static class MauiProgram
{
	public static MauiApp CreateMauiApp()
	{
		Registrar.Registered.Register(typeof(TrackingImageSource), typeof(TrackingImageSourceHandler));

		var builder = MauiApp.CreateBuilder();
		builder.UseMauiApp<App>();

		return builder.Build();
	}
}
