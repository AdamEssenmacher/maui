using Microsoft.Maui;
using Microsoft.Maui.Controls.Hosting;
using Microsoft.Maui.Hosting;

namespace ShellTitleViewReplacementLeakRepro;

public static class MauiProgram
{
	public static MauiApp CreateMauiApp()
	{
		var builder = MauiApp.CreateBuilder();

		builder
			.UseMauiApp<App>()
			.ConfigureMauiHandlers(handlers =>
			{
				handlers.AddHandler<TrackedTitleView, TrackedTitleViewHandler>();
			});

		return builder.Build();
	}
}
