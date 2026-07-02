using Microsoft.Maui;
using Microsoft.Maui.Hosting;

namespace RelativeBindingTemplatedParentPendingRetentionRepro;

public static class MauiProgram
{
	public static MauiApp CreateMauiApp() =>
		MauiApp
			.CreateBuilder()
			.UseMauiApp<App>()
			.Build();
}
