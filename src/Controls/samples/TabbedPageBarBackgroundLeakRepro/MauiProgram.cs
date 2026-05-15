using Microsoft.Maui.Hosting;

namespace Maui.Controls.Sample.TabbedPageBarBackgroundLeakRepro;

public static class MauiProgram
{
	public static MauiApp CreateMauiApp() =>
		MauiApp
			.CreateBuilder()
			.UseMauiApp<App>()
			.Build();
}
