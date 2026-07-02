#pragma warning disable CS0618 // This repro intentionally exercises a legacy compatibility renderer.

using Microsoft.Maui.Controls.Compatibility.Hosting;
using Microsoft.Maui.Controls.Hosting;
using Microsoft.Maui.Hosting;

namespace ScrollViewRendererPendingScrollRetentionLeakRepro;

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
