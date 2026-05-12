using Microsoft.Maui.Hosting;

namespace MauiWindowFrameObserverLeakRepro;

public static class MauiProgram
{
	public static MauiApp CreateMauiApp()
	{
		var builder = MauiApp.CreateBuilder();

		builder
			.UseMauiApp<App>()
			.ConfigureMauiHandlers(handlers =>
			{
				handlers.AddHandler<ContentPage, Microsoft.Maui.Handlers.PageHandler>();
				handlers.AddHandler<IContentView, Microsoft.Maui.Handlers.ContentViewHandler>();
				handlers.AddHandler<Layout, Microsoft.Maui.Handlers.LayoutHandler>();
				handlers.AddHandler<ScrollView, Microsoft.Maui.Handlers.ScrollViewHandler>();
				handlers.AddHandler<Label, Microsoft.Maui.Handlers.LabelHandler>();
			});

		return builder.Build();
	}
}
