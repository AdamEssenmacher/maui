using Android.App;
using Android.Runtime;
using Microsoft.Maui;
using Microsoft.Maui.Hosting;

namespace MapElementsSyncPerfRepro;

[Application]
public class MainApplication : MauiApplication
{
	public MainApplication(IntPtr handle, JniHandleOwnership ownership) : base(handle, ownership)
	{
	}

	protected override MauiApp CreateMauiApp()
	{
#if MAP_ELEMENTS_SYNC_PERF_REPRO_AUTORUN
		AutoRunSettings.Enable();
#endif

		return MauiProgram.CreateMauiApp();
	}
}
