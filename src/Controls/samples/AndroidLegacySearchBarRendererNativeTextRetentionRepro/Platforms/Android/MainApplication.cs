#nullable enable

using Android.App;
using Android.Runtime;
using Microsoft.Maui;

namespace AndroidLegacySearchBarRendererNativeTextRetentionRepro;

[Application]
public sealed class MainApplication : MauiApplication
{
	public MainApplication(IntPtr handle, JniHandleOwnership ownership)
		: base(handle, ownership)
	{
	}

	protected override MauiApp CreateMauiApp()
	{
		return MauiProgram.CreateMauiApp();
	}
}
