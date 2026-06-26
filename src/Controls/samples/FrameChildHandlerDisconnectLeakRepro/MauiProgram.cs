using Microsoft.Maui.Controls.Hosting;
using Microsoft.Maui.Hosting;

namespace FrameChildHandlerDisconnectLeakRepro;

public static class MauiProgram
{
	public static MauiApp CreateMauiApp()
	{
		return MauiApp.CreateBuilder()
			.UseMauiApp<App>()
			.ConfigureMauiHandlers(handlers =>
			{
#pragma warning disable CS0618 // FrameRenderer is the obsolete compatibility handler under test.
				handlers.AddHandler<Microsoft.Maui.Controls.Frame, Microsoft.Maui.Controls.Handlers.Compatibility.FrameRenderer>();
#pragma warning restore CS0618
				handlers.AddHandler<PayloadFrameContentView, PayloadFrameContentViewHandler>();
			})
			.ConfigureFonts(fonts => { })
			.Build();
	}
}
