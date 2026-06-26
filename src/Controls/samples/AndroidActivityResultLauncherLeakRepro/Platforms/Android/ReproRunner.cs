using Android.Content;
using Android.OS;

namespace AndroidActivityResultLauncherLeakRepro;

static class ReproRunner
{
	const string ResultFileName = "autorun-results.txt";

	public static async Task RunAsync(MainActivity activity)
	{
		try
		{
			var control = await RunScenarioAsync(activity, "control-unregister-and-clear", clearAfterDestroy: true);
			var leak = await RunScenarioAsync(activity, "current-static-launcher", clearAfterDestroy: false);

			var proven =
				!control.LauncherReferencesProbeActivity &&
				leak.ActivityAlive &&
				leak.PayloadAlive &&
				leak.LauncherReferencesProbeActivity;

			var lines = new[]
			{
				$"RESULT: {(proven ? "PROVEN" : "INCONCLUSIVE")}",
				control.ToString(),
				leak.ToString(),
				$"payloadBytesPerActivity={ProbeActivity.PayloadBytes}",
				$"payloadMiBPerActivity={ProbeActivity.PayloadBytes / 1024d / 1024d:F1}",
				$"launcherRoot={PhotoPickerRegistrationProbe.DescribeLaunchers()}",
				$"dotnet-version={System.Environment.Version}"
			};

			WriteResults(activity, lines);
		}
		catch (Exception ex)
		{
			WriteResults(activity, ["RESULT: ERROR", ex.ToString()]);
		}
		finally
		{
			await Task.Delay(250);
			global::Android.OS.Process.KillProcess(global::Android.OS.Process.MyPid());
		}
	}

	static async Task<ScenarioResult> RunScenarioAsync(MainActivity activity, string name, bool clearAfterDestroy)
	{
		PhotoPickerRegistrationProbe.ClearAll(unregister: true);
		ProbeActivity.Prepare();

		activity.StartActivity(ProbeActivity.CreateIntent(activity, name));
		await ProbeActivity.WaitForDestroyedAsync(TimeSpan.FromSeconds(10));

		if (clearAfterDestroy)
			PhotoPickerRegistrationProbe.ClearAll(unregister: true);

		DrainActivity.Prepare();
		activity.StartActivity(DrainActivity.CreateIntent(activity));
		await DrainActivity.WaitForDestroyedAsync(TimeSpan.FromSeconds(10));

		await Task.Delay(500);
		ForceFullGc();

		var activityAlive = ProbeActivity.LastActivity?.IsAlive == true;
		var payloadAlive = ProbeActivity.LastPayload?.IsAlive == true;
		var launcherRoot = PhotoPickerRegistrationProbe.DescribeLaunchers();
		var activityRoot = PhotoPickerRegistrationProbe.InspectLauncherActivityRoot(ProbeActivity.LastActivityIdentityHash);

		return new ScenarioResult(name, clearAfterDestroy, activityAlive, payloadAlive, activityRoot.ReferencesExpectedActivity, launcherRoot, activityRoot.Description);
	}

	static void ForceFullGc()
	{
		for (var i = 0; i < 5; i++)
		{
			GC.Collect();
			GC.WaitForPendingFinalizers();
			GC.Collect();
			Java.Lang.JavaSystem.Gc();
			Thread.Sleep(100);
		}
	}

	static void WriteResults(Context context, IEnumerable<string> lines)
	{
		var path = Path.Combine(context.FilesDir!.AbsolutePath, ResultFileName);
		File.WriteAllLines(path, lines);
	}

	readonly record struct ScenarioResult(
		string Name,
		bool ClearedLauncher,
		bool ActivityAlive,
		bool PayloadAlive,
		bool LauncherReferencesProbeActivity,
		string LauncherRoot,
		string LauncherActivityRoot)
	{
		public override string ToString() =>
			$"{Name}: clearedLauncher={ClearedLauncher}, activityAlive={ActivityAlive}, payloadAlive={PayloadAlive}, launcherReferencesProbeActivity={LauncherReferencesProbeActivity}, launcherRoot={LauncherRoot}, launcherActivityRoot={LauncherActivityRoot}";
	}
}
