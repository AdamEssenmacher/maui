using Microsoft.Maui;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Controls.Hosting;
using Microsoft.Maui.Controls.Handlers.Items;
using Microsoft.Maui.Hosting;

namespace CollectionViewHeaderFooterDisconnectLeakRepro;

public static class MauiProgram
{
	public static MauiApp CreateMauiApp()
	{
		var builder = MauiApp.CreateBuilder();

		builder
			.UseMauiApp<App>()
			.ConfigureMauiHandlers(handlers =>
			{
				handlers.AddHandler<CollectionView, CollectionViewHandler>();
				handlers.AddHandler<TrackedHeaderView, TrackedHeaderViewHandler>();
			});

		return builder.Build();
	}
}
