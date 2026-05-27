using Android.App;
using Android.Runtime;
using Microsoft.Maui;
using Microsoft.Maui.Hosting;

namespace MapGeopathAppendRepro;

[Application]
public class MainApplication : MauiApplication
{
	public MainApplication(IntPtr handle, JniHandleOwnership ownership) : base(handle, ownership)
	{
	}

	protected override MauiApp CreateMauiApp()
	{
#if MAP_GEOPATH_APPEND_REPRO_AUTORUN
		AutoRunSettings.Enable();
#endif

		return MauiProgram.CreateMauiApp();
	}
}
