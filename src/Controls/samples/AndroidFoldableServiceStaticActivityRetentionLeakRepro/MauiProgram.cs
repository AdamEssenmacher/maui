using Microsoft.Maui.Controls.Hosting;
using Microsoft.Maui.Foldable;
using Microsoft.Maui.Hosting;

namespace AndroidFoldableServiceStaticActivityRetentionLeakRepro;

public static class MauiProgram
{
	public static MauiApp CreateMauiApp()
	{
		var builder = MauiApp.CreateBuilder();
		builder
			.UseMauiApp<App>()
			.UseFoldable();

		return builder.Build();
	}
}
