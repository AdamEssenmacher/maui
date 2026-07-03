using Microsoft.Maui.Hosting;

namespace PageInternalChildrenClearRetentionRepro;

public static class MauiProgram
{
	public static MauiApp CreateMauiApp()
	{
		return MauiApp
			.CreateBuilder()
			.UseMauiApp<App>()
			.Build();
	}
}
