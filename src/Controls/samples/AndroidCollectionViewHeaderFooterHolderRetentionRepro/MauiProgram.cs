#nullable enable

using Microsoft.Maui;
using Microsoft.Maui.Controls.Hosting;
using Microsoft.Maui.Hosting;

namespace AndroidCollectionViewHeaderFooterHolderRetentionRepro;

public static class MauiProgram
{
	public static MauiApp CreateMauiApp()
	{
		var builder = MauiApp.CreateBuilder();

		builder.UseMauiApp<App>();

		return builder.Build();
	}
}
