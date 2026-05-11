using Microsoft.Maui.Hosting;

namespace MauiSoftInputObserverLeakRepro;

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
				handlers.AddHandler<Entry, Microsoft.Maui.Handlers.EntryHandler>();
				handlers.AddHandler<Label, Microsoft.Maui.Handlers.LabelHandler>();
				handlers.AddHandler<Button, Microsoft.Maui.Handlers.ButtonHandler>();
			});

		return builder.Build();
	}
}
