#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Android.App;
using Android.Content;
using Android.OS;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Storage;
using Environment = System.Environment;

namespace IntermediateActivityPendingTaskLeakRepro;

public class MainPage : ContentPage
{
	const int Iterations = 80;
	const int PayloadBytes = 1024 * 1024;
	const int ControlRequestCode = 48126;

	static readonly Type IntermediateActivityType =
		typeof(Platform).Assembly.GetType("Microsoft.Maui.ApplicationModel.IntermediateActivity")
		?? throw new InvalidOperationException("IntermediateActivity type was not found.");

	static readonly MethodInfo StartAsyncMethod =
		IntermediateActivityType.GetMethod("StartAsync", BindingFlags.Public | BindingFlags.Static)
		?? throw new InvalidOperationException("IntermediateActivity.StartAsync was not found.");

	static readonly object PendingTasks =
		IntermediateActivityType.GetField("pendingTasks", BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null)
		?? throw new InvalidOperationException("IntermediateActivity.pendingTasks was not found.");

	static readonly PropertyInfo PendingTasksCount =
		PendingTasks.GetType().GetProperty("Count")
		?? throw new InvalidOperationException("pendingTasks.Count was not found.");

	static readonly MethodInfo PendingTasksClear =
		PendingTasks.GetType().GetMethod("Clear")
		?? throw new InvalidOperationException("pendingTasks.Clear was not found.");

	readonly Label _status;

	public MainPage()
	{
		_status = new Label
		{
			Text = "Running IntermediateActivity pending task leak repro...",
			Margin = 24
		};

		Content = _status;
		Loaded += OnLoaded;
	}

	async void OnLoaded(object? sender, EventArgs e)
	{
		Loaded -= OnLoaded;

		var result = await RunScenariosAsync();
		var text = result.ToString();
		_status.Text = text;

		var resultsPath = Path.Combine(FileSystem.AppDataDirectory, "autorun-results.txt");
		File.WriteAllText(resultsPath, text);
		Android.Util.Log.Info("IntermediateActivityPendingTaskLeakRepro", text);

		await Task.Delay(250);
		Process.KillProcess(Process.MyPid());
	}

	static async Task<ReproResult> RunScenariosAsync()
	{
		ClearPendingTasks();
		var controlStartCount = PendingTaskCount;
		var controlRefs = await RunCompletedLaunchControlAsync();
		await ForceGcAsync();
		var control = new ScenarioResult(
			"completed-launch-control",
			CountAlive(controlRefs),
			Iterations,
			PendingTaskCount - controlStartCount,
			0);

		ClearPendingTasks();
		var failingStartCount = PendingTaskCount;
		var failing = RunForcedLaunchFailureScenario(failingStartCount);
		await ForceGcAsync();
		failing = failing with { PayloadsAlive = CountAlive(failing.PayloadRefs) };

		var result = new ReproResult(control, failing);
		ClearPendingTasks();
		MainActivity.ThrowIntermediateLaunchFailures = false;
		return result;
	}

	static async Task<List<WeakReference>> RunCompletedLaunchControlAsync()
	{
		var refs = new List<WeakReference>();

		for (var i = 0; i < Iterations; i++)
		{
			refs.Add(await RunCompletedLaunchIterationAsync());
		}

		return refs;
	}

	static async Task<WeakReference> RunCompletedLaunchIterationAsync()
	{
		var activity = Platform.CurrentActivity ?? throw new InvalidOperationException("No current Activity.");
		var payload = new Payload(PayloadBytes);
		var intent = new Intent(activity, typeof(NoopResultActivity));

		await StartIntermediateActivityAsync(
			intent,
			ControlRequestCode,
			_ => payload.Touch(),
			_ => payload.Touch());

		return new WeakReference(payload);
	}

	static FailureScenarioResult RunForcedLaunchFailureScenario(int startCount)
	{
		var refs = new List<WeakReference>();
		var exceptions = 0;
		MainActivity.ThrowIntermediateLaunchFailures = true;

		for (var i = 0; i < Iterations; i++)
		{
			refs.Add(RunForcedLaunchFailureIteration(ref exceptions));
		}

		MainActivity.ThrowIntermediateLaunchFailures = false;

		return new FailureScenarioResult(
			"forced-launch-failure",
			0,
			Iterations,
			PendingTaskCount - startCount,
			exceptions,
			refs);
	}

	static WeakReference RunForcedLaunchFailureIteration(ref int exceptions)
	{
		var activity = Platform.CurrentActivity ?? throw new InvalidOperationException("No current Activity.");
		var payload = new Payload(PayloadBytes);
		var intent = new Intent(activity, typeof(NoopResultActivity));

		try
		{
			_ = StartIntermediateActivityAsync(
				intent,
				MainActivity.FailingRequestCode,
				_ => payload.Touch(),
				_ => payload.Touch());
		}
		catch (TargetInvocationException ex) when (ex.InnerException is ActivityNotFoundException)
		{
			exceptions++;
		}

		return new WeakReference(payload);
	}

	static Task<Intent> StartIntermediateActivityAsync(
		Intent intent,
		int requestCode,
		Action<Intent> onCreate,
		Action<Intent> onResult)
	{
		var task = StartAsyncMethod.Invoke(null, new object?[] { intent, requestCode, onCreate, onResult });
		return (Task<Intent>)task!;
	}

	static int PendingTaskCount => (int)PendingTasksCount.GetValue(PendingTasks)!;

	static void ClearPendingTasks() => PendingTasksClear.Invoke(PendingTasks, null);

	static async Task ForceGcAsync()
	{
		for (var i = 0; i < 4; i++)
		{
			GC.Collect();
			GC.WaitForPendingFinalizers();
			GC.Collect();
			Java.Lang.JavaSystem.Gc();
			await Task.Delay(250);
		}
	}

	static int CountAlive(List<WeakReference> refs)
	{
		var count = 0;
		foreach (var reference in refs)
		{
			if (reference.IsAlive)
				count++;
		}

		return count;
	}

	sealed class Payload
	{
		readonly byte[] _data;
		int _ticks;

		public Payload(int bytes)
		{
			_data = new byte[bytes];
			_data[0] = 123;
		}

		public void Touch()
		{
			_ticks++;
			if (_ticks == int.MaxValue)
				_ticks = _data[0];
		}
	}

	readonly record struct ScenarioResult(
		string Name,
		int PayloadsAlive,
		int Total,
		int PendingTaskDelta,
		int Exceptions)
	{
		public override string ToString()
		{
			var builder = new StringBuilder();
			builder.Append(Name);
			builder.Append(": payloads=");
			builder.Append(PayloadsAlive);
			builder.Append('/');
			builder.Append(Total);
			builder.Append(", pending-task-delta=");
			builder.Append(PendingTaskDelta);
			builder.Append(", exceptions=");
			builder.Append(Exceptions);
			return builder.ToString();
		}
	}

	readonly record struct FailureScenarioResult(
		string Name,
		int PayloadsAlive,
		int Total,
		int PendingTaskDelta,
		int Exceptions,
		List<WeakReference> PayloadRefs)
	{
		public override string ToString()
		{
			var builder = new StringBuilder();
			builder.Append(Name);
			builder.Append(": payloads=");
			builder.Append(PayloadsAlive);
			builder.Append('/');
			builder.Append(Total);
			builder.Append(", pending-task-delta=");
			builder.Append(PendingTaskDelta);
			builder.Append(", exceptions=");
			builder.Append(Exceptions);
			return builder.ToString();
		}
	}

	readonly record struct ReproResult(ScenarioResult Control, FailureScenarioResult Failure)
	{
		public bool IsProven =>
			Control.PayloadsAlive == 0 &&
			Control.PendingTaskDelta == 0 &&
			Failure.PayloadsAlive >= Iterations / 2 &&
			Failure.PendingTaskDelta == Iterations &&
			Failure.Exceptions == Iterations;

		public override string ToString()
		{
			var builder = new StringBuilder();
			builder.AppendLine(IsProven ? "RESULT: PROVEN" : "RESULT: NOT PROVEN");
			builder.AppendLine(Control.ToString());
			builder.AppendLine(Failure.ToString());
			builder.Append("payload-bytes-per-scenario=");
			builder.Append(Iterations * PayloadBytes);
			builder.AppendLine();
			builder.Append("app-data-directory=");
			builder.Append(FileSystem.AppDataDirectory);
			builder.AppendLine();
			builder.Append("dotnet-version=");
			builder.Append(Environment.Version);
			return builder.ToString();
		}
	}
}
