using Microsoft.Maui;
using Microsoft.Maui.Controls.Hosting;
using Microsoft.Maui.Hosting;

namespace AndroidListViewActionModeMenuStateRetentionRepro;

public static class MauiProgram
{
	public static MauiApp CreateMauiApp()
	{
		var builder = MauiApp.CreateBuilder();

		builder
			.UseMauiApp<App>()
			.ConfigureImageSources(services =>
			{
				services.AddService<PayloadImageSource>(_ => new PayloadImageSourceService());
			});

		return builder.Build();
	}
}
