using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Runtime;

namespace AndroidActivityResultLauncherLeakRepro;

[Activity(
	Theme = "@style/Maui.SplashTheme",
	NoHistory = true,
	ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
[Register("com.microsoft.maui.androidactivityresultlauncherleakrepro.DrainActivity")]
public sealed class DrainActivity : Activity
{
	static TaskCompletionSource? s_destroyed;

	public static void Prepare()
	{
		s_destroyed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
	}

	public static Intent CreateIntent(Activity owner) =>
		new Intent(owner, typeof(DrainActivity));

	public static async Task WaitForDestroyedAsync(TimeSpan timeout)
	{
		var task = s_destroyed?.Task ?? Task.CompletedTask;
		var timeoutTask = Task.Delay(timeout);

		if (await Task.WhenAny(task, timeoutTask) != task)
			throw new TimeoutException("DrainActivity did not destroy before timeout.");
	}

	protected override void OnCreate(Bundle? savedInstanceState)
	{
		base.OnCreate(savedInstanceState);
		Finish();
	}

	protected override void OnDestroy()
	{
		base.OnDestroy();
		s_destroyed?.TrySetResult();
		s_destroyed = null;
	}
}
