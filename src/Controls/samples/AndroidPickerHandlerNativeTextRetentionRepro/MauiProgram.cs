#nullable enable

using System;
using Microsoft.Maui.Controls.Hosting;
using Microsoft.Maui.Hosting;

namespace AndroidPickerHandlerNativeTextRetentionRepro;

public static class MauiProgram
{
	public static MauiApp CreateMauiApp()
	{
		AppContext.SetSwitch("Microsoft.Maui.RuntimeFeature.IsMaterial3Enabled", true);

		var builder = MauiApp.CreateBuilder();
		builder.UseMauiApp<App>();
		return builder.Build();
	}
}
