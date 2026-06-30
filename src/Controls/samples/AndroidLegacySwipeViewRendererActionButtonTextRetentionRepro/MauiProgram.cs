#nullable enable

using Microsoft.Maui.Controls.Compatibility.Hosting;
using Microsoft.Maui.Controls.Hosting;
using Microsoft.Maui.Hosting;

namespace AndroidLegacySwipeViewRendererActionButtonTextRetentionRepro;

public static class MauiProgram
{
	public static MauiApp CreateMauiApp()
	{
		var builder = MauiApp.CreateBuilder();
		builder
			.UseMauiCompatibility()
			.UseMauiApp<App>();
		return builder.Build();
	}
}
