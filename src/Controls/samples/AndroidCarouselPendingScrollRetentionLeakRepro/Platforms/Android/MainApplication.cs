using Android.App;
using Android.Runtime;
using Microsoft.Maui;
using Microsoft.Maui.Hosting;

namespace AndroidCarouselPendingScrollRetentionLeakRepro;

[Application]
public sealed class MainApplication : MauiApplication
{
	public MainApplication(nint handle, JniHandleOwnership ownership)
		: base(handle, ownership)
	{
	}

	protected override MauiApp CreateMauiApp()
	{
		return MauiProgram.CreateMauiApp();
	}
}
