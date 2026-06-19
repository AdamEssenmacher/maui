using Android.App;
using Android.Content.PM;
using Android.OS;
using Android.Runtime;
using Microsoft.Maui;

namespace ActivityStateManagerLeakRepro;

[Activity(
	Theme = "@style/Maui.SplashTheme",
	MainLauncher = true,
	ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
[Register("com.microsoft.maui.activitystatemanagerleakrepro.MainActivity")]
public sealed class MainActivity : MauiAppCompatActivity
{
	static readonly object Sync = new();
	static WeakReference<MainActivity>? _currentActivity;
	static long _nextInstanceId;
	static long _currentInstanceId;
	static ActivityTransitionState _lastState;
	readonly long _instanceId;

	public MainActivity()
	{
		_instanceId = Interlocked.Increment(ref _nextInstanceId);
	}

	internal static event EventHandler<ActivityTransition>? Transitioned;

	public static long CurrentInstanceId => Interlocked.Read(ref _currentInstanceId);

	public static MainActivity? Current
	{
		get
		{
			lock (Sync)
				return _currentActivity is not null && _currentActivity.TryGetTarget(out var activity) ? activity : null;
		}
	}

	public static Task WaitForNextResumeAsync(long previousInstanceId, CancellationToken token)
	{
		var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

		EventHandler<ActivityTransition>? handler = null;
		handler = (_, transition) =>
		{
			if (transition.State == ActivityTransitionState.Resumed && transition.InstanceId > previousInstanceId)
				tcs.TrySetResult();
		};

		Transitioned += handler;

		if (IsResumedAfter(previousInstanceId))
			tcs.TrySetResult();

		return AwaitAndUnsubscribeAsync();

		async Task AwaitAndUnsubscribeAsync()
		{
			using var registration = token.Register(() => tcs.TrySetCanceled(token));

			try
			{
				await tcs.Task;
			}
			finally
			{
				Transitioned -= handler;
			}
		}
	}

	protected override void OnCreate(Bundle? savedInstanceState)
	{
		SetCurrent(this, ActivityTransitionState.Created);
		base.OnCreate(savedInstanceState);
		Notify(ActivityTransitionState.Created);
	}

	protected override void OnStart()
	{
		base.OnStart();
		SetCurrent(this, ActivityTransitionState.Started);
		Notify(ActivityTransitionState.Started);
	}

	protected override void OnResume()
	{
		base.OnResume();
		SetCurrent(this, ActivityTransitionState.Resumed);
		Notify(ActivityTransitionState.Resumed);
	}

	protected override void OnPause()
	{
		Notify(ActivityTransitionState.Paused);
		base.OnPause();
	}

	protected override void OnStop()
	{
		Notify(ActivityTransitionState.Stopped);
		base.OnStop();
	}

	protected override void OnDestroy()
	{
		Notify(ActivityTransitionState.Destroyed);
		base.OnDestroy();

		lock (Sync)
		{
			if (_currentActivity is not null &&
				_currentActivity.TryGetTarget(out var activity) &&
				ReferenceEquals(activity, this))
			{
				_currentActivity = null;
			}
		}
	}

	static bool IsResumedAfter(long previousInstanceId)
	{
		lock (Sync)
			return _currentInstanceId > previousInstanceId && _lastState == ActivityTransitionState.Resumed;
	}

	static void SetCurrent(MainActivity activity, ActivityTransitionState state)
	{
		lock (Sync)
		{
			_currentActivity = new WeakReference<MainActivity>(activity);
			_currentInstanceId = activity._instanceId;
			_lastState = state;
		}
	}

	void Notify(ActivityTransitionState state)
	{
		lock (Sync)
			_lastState = state;

		Transitioned?.Invoke(null, new ActivityTransition(state, _instanceId));
	}
}

internal enum ActivityTransitionState
{
	Created,
	Started,
	Resumed,
	Paused,
	Stopped,
	Destroyed
}

internal sealed record ActivityTransition(ActivityTransitionState State, long InstanceId);
