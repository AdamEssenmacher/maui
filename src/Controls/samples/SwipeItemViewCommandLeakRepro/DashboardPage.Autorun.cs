namespace SwipeItemViewCommandLeakRepro;

public sealed partial class DashboardPage
{
#if REPRO_AUTORUN
	bool _autorunStarted;

	protected override async void OnAppearing()
	{
		base.OnAppearing();

		if (_autorunStarted)
			return;

		_autorunStarted = true;
		await Task.Delay(500);

		await RunAsync(ReproMode.SwipeItemViewCommand);
		await Task.Delay(500);
		await RunAsync(ReproMode.PlainSwipeItemControl);
		await Task.Delay(500);
		await RunAsync(ReproMode.ClearCommandOnDisappear);

		const string line = "SWIPE_REPRO_AUTORUN_COMPLETE";
		Console.WriteLine(line);
		System.Diagnostics.Debug.WriteLine(line);
#if ANDROID
		Android.Util.Log.Info("SwipeRepro", line);
#endif

		await Task.Delay(500);
		Environment.Exit(0);
	}
#endif
}
