#nullable enable

using Microsoft.Maui;
using Microsoft.Maui.Controls.Compatibility.Hosting;
using Microsoft.Maui.Hosting;

namespace AndroidLegacyEntryRendererNativeTextRetentionRepro;

public static class MauiProgram
{
	public static MauiApp CreateMauiApp()
	{
		var builder = MauiApp.CreateBuilder();
		builder
			.UseMauiApp<App>()
			.UseMauiCompatibility();

		return builder.Build();
	}
}
