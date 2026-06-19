using Android.App;
using Android.OS;
using Android.Runtime;
using Microsoft.Maui;
using Microsoft.Maui.Hosting;
using AndroidApplication = Android.App.Application;

namespace ActivityStateManagerLeakRepro;

[Application]
public sealed class MainApplication : MauiApplication
{
	public MainApplication(IntPtr handle, JniHandleOwnership ownership) : base(handle, ownership)
	{
	}

	protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();

	public override void OnCreate()
	{
		base.OnCreate();
		RegisterActivityLifecycleCallbacks(new CountingActivityLifecycleCallbacks());
	}

	public override void RegisterActivityLifecycleCallbacks(AndroidApplication.IActivityLifecycleCallbacks? callback)
	{
		base.RegisterActivityLifecycleCallbacks(callback);
		ReproMetrics.RecordCallbackRegistration(callback);
	}

	public override void UnregisterActivityLifecycleCallbacks(AndroidApplication.IActivityLifecycleCallbacks? callback)
	{
		ReproMetrics.RecordCallbackUnregistration(callback);
		base.UnregisterActivityLifecycleCallbacks(callback);
	}

	sealed class CountingActivityLifecycleCallbacks : Java.Lang.Object, AndroidApplication.IActivityLifecycleCallbacks
	{
		public void OnActivityCreated(Activity activity, Bundle? savedInstanceState) =>
			ReproMetrics.RecordActualLifecycleCallbackEvent();

		public void OnActivityDestroyed(Activity activity) =>
			ReproMetrics.RecordActualLifecycleCallbackEvent();

		public void OnActivityPaused(Activity activity) =>
			ReproMetrics.RecordActualLifecycleCallbackEvent();

		public void OnActivityResumed(Activity activity) =>
			ReproMetrics.RecordActualLifecycleCallbackEvent();

		public void OnActivitySaveInstanceState(Activity activity, Bundle outState) =>
			ReproMetrics.RecordActualLifecycleCallbackEvent();

		public void OnActivityStarted(Activity activity) =>
			ReproMetrics.RecordActualLifecycleCallbackEvent();

		public void OnActivityStopped(Activity activity) =>
			ReproMetrics.RecordActualLifecycleCallbackEvent();
	}
}
