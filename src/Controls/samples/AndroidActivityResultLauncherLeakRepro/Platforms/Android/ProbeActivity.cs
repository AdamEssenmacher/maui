using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Runtime;
using AndroidX.Activity;

namespace AndroidActivityResultLauncherLeakRepro;

[Activity(
	Theme = "@style/Maui.SplashTheme",
	NoHistory = true,
	ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
[Register("com.microsoft.maui.androidactivityresultlauncherleakrepro.ProbeActivity")]
public sealed class ProbeActivity : ComponentActivity
{
	public const int PayloadBytes = 80 * 1024 * 1024;
	const string ScenarioExtra = "scenario";

	static TaskCompletionSource? s_destroyed;

	byte[]? _payload;

	public static WeakReference? LastActivity { get; private set; }
	public static WeakReference? LastPayload { get; private set; }
	public static int LastActivityIdentityHash { get; private set; }

	public static void Prepare()
	{
		s_destroyed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		LastActivity = null;
		LastPayload = null;
		LastActivityIdentityHash = 0;
	}

	public static Intent CreateIntent(Activity owner, string scenario) =>
		new Intent(owner, typeof(ProbeActivity)).PutExtra(ScenarioExtra, scenario);

	public static async Task WaitForDestroyedAsync(TimeSpan timeout)
	{
		var task = s_destroyed?.Task ?? Task.CompletedTask;
		var timeoutTask = Task.Delay(timeout);

		if (await Task.WhenAny(task, timeoutTask) != task)
			throw new TimeoutException("ProbeActivity did not destroy before timeout.");
	}

	protected override void OnCreate(Bundle? savedInstanceState)
	{
		base.OnCreate(savedInstanceState);

		_payload = new byte[PayloadBytes];
		for (var i = 0; i < _payload.Length; i += 4096)
			_payload[i] = (byte)(i % 251);

		LastActivity = new WeakReference(this);
		LastPayload = new WeakReference(_payload);
		LastActivityIdentityHash = Java.Lang.JavaSystem.IdentityHashCode(this);

		PhotoPickerRegistrationProbe.RegisterAll(this);

		Finish();
	}

	protected override void OnDestroy()
	{
		base.OnDestroy();
		s_destroyed?.TrySetResult();
		s_destroyed = null;
	}
}
