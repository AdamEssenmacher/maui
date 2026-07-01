#nullable enable

using Microsoft.Maui;
using Microsoft.AspNetCore.Components.WebView.Maui;
using Microsoft.Maui.Controls.Hosting;
using Microsoft.Maui.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace AndroidBlazorWebViewNativeDestroyRetentionRepro;

public static class MauiProgram
{
	public static MauiApp CreateMauiApp()
	{
		var builder = MauiApp.CreateBuilder();
		builder.UseMauiApp<App>();
		builder.Services.AddMauiBlazorWebView();
		return builder.Build();
	}
}
