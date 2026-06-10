using Android.App;
using Android.Runtime;
using Microsoft.Maui;
using Microsoft.Maui.Hosting;

namespace PathDataSharedGeometryLeakRepro;

[Application]
public class MainApplication : MauiApplication
{
	public MainApplication(IntPtr handle, JniHandleOwnership ownership) : base(handle, ownership)
	{
	}

	protected override MauiApp CreateMauiApp()
	{
#if PATH_DATA_SHARED_GEOMETRY_LEAK_REPRO_AUTORUN
		AutoRunSettings.Enable();
#endif

		return MauiProgram.CreateMauiApp();
	}
}
