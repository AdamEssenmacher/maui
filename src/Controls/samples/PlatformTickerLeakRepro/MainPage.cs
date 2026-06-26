#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Android.OS;
using Android.Runtime;
using Microsoft.Maui.Animations;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Platform;
using Microsoft.Maui.Storage;
using Environment = System.Environment;

namespace PlatformTickerLeakRepro;

public class MainPage : ContentPage
{
	const int Iterations = 80;
	const int PayloadBytes = 1024 * 1024;

	readonly Label _status;

	public MainPage()
	{
		_status = new Label
		{
			Text = "Running PlatformTicker leak repro...",
			Margin = 24
		};

		Content = _status;
		Loaded += OnLoaded;
	}

	async void OnLoaded(object? sender, EventArgs e)
	{
		Loaded -= OnLoaded;

		var result = await Task.Run(RunScenarios);
		var text = result.ToString();
		_status.Text = text;

		var resultsPath = Path.Combine(FileSystem.AppDataDirectory, "autorun-results.txt");
		File.WriteAllText(resultsPath, text);
		Android.Util.Log.Info("PlatformTickerLeakRepro", text);

		await Task.Delay(250);
		Process.KillProcess(Process.MyPid());
	}

	static ReproResult RunScenarios()
	{
		var control = RunDirectTickerScenario(stopBeforeDispose: true);
		var runningDisposed = RunDirectTickerScenario(stopBeforeDispose: false);
		var managerDisposed = RunAnimationManagerScenario();

		return new ReproResult(control, runningDisposed, managerDisposed);
	}

	static ScenarioResult RunDirectTickerScenario(bool stopBeforeDispose)
	{
		var payloadRefs = new List<WeakReference>();
		var tickerRefs = new List<WeakReference>();

		RunOnMainThread(() =>
		{
			for (var i = 0; i < Iterations; i++)
			{
				var payload = new Payload(PayloadBytes);
				var ticker = new PlatformTicker(new DummyEnergySaverListenerManager());
				ticker.Fire = payload.Touch;
				ticker.Start();

				if (stopBeforeDispose)
					ticker.Stop();

				ticker.Dispose();

				payloadRefs.Add(new WeakReference(payload));
				tickerRefs.Add(new WeakReference(ticker));
			}
		});

		Thread.Sleep(1000);
		ForceGc();

		return new ScenarioResult(
			stopBeforeDispose ? "direct-stop-before-dispose-control" : "direct-running-dispose",
			CountAlive(payloadRefs),
			CountAlive(tickerRefs),
			Iterations);
	}

	static ScenarioResult RunAnimationManagerScenario()
	{
		var payloadRefs = new List<WeakReference>();
		var managerRefs = new List<WeakReference>();
		var tickerRefs = new List<WeakReference>();

		RunOnMainThread(() =>
		{
			for (var i = 0; i < Iterations; i++)
			{
				var payload = new Payload(PayloadBytes);
				var ticker = new PlatformTicker(new DummyEnergySaverListenerManager());
				var manager = new AnimationManager(ticker);
				var animation = new Microsoft.Maui.Animations.Animation(_ => payload.Touch(), duration: 3600);

				manager.Add(animation);
				manager.Dispose();

				payloadRefs.Add(new WeakReference(payload));
				managerRefs.Add(new WeakReference(manager));
				tickerRefs.Add(new WeakReference(ticker));
			}
		});

		Thread.Sleep(1000);
		ForceGc();

		return new ScenarioResult(
			"animation-manager-running-dispose",
			CountAlive(payloadRefs),
			CountAlive(tickerRefs),
			Iterations,
			CountAlive(managerRefs));
	}

	static void RunOnMainThread(Action action)
	{
		using var completed = new ManualResetEventSlim();
		Exception? exception = null;

		MainThread.BeginInvokeOnMainThread(() =>
		{
			try
			{
				action();
			}
			catch (Exception ex)
			{
				exception = ex;
			}
			finally
			{
				completed.Set();
			}
		});

		completed.Wait();

		if (exception is not null)
			throw exception;
	}

	static void ForceGc()
	{
		for (var i = 0; i < 4; i++)
		{
			GC.Collect();
			GC.WaitForPendingFinalizers();
			GC.Collect();
			Java.Lang.JavaSystem.Gc();
			Thread.Sleep(250);
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

	sealed class DummyEnergySaverListenerManager : IEnergySaverListenerManager
	{
		public void Add(IEnergySaverListener listener) => listener.OnStatusUpdated(false);

		public void Remove(IEnergySaverListener listener)
		{
		}
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

	readonly record struct ScenarioResult(string Name, int PayloadsAlive, int TickersAlive, int Total, int ManagersAlive = -1)
	{
		public override string ToString()
		{
			var builder = new StringBuilder();
			builder.Append(Name);
			builder.Append(": payloads=");
			builder.Append(PayloadsAlive);
			builder.Append('/');
			builder.Append(Total);
			builder.Append(", tickers=");
			builder.Append(TickersAlive);
			builder.Append('/');
			builder.Append(Total);

			if (ManagersAlive >= 0)
			{
				builder.Append(", managers=");
				builder.Append(ManagersAlive);
				builder.Append('/');
				builder.Append(Total);
			}

			return builder.ToString();
		}
	}

	readonly record struct ReproResult(ScenarioResult Control, ScenarioResult RunningDisposed, ScenarioResult ManagerDisposed)
	{
		public bool IsProven =>
			Control.PayloadsAlive == 0 &&
			RunningDisposed.PayloadsAlive >= TotalLeakThreshold &&
			ManagerDisposed.PayloadsAlive >= TotalLeakThreshold;

		static int TotalLeakThreshold => Iterations / 2;

		public override string ToString()
		{
			var builder = new StringBuilder();
			builder.AppendLine(IsProven ? "RESULT: PROVEN" : "RESULT: NOT PROVEN");
			builder.AppendLine(Control.ToString());
			builder.AppendLine(RunningDisposed.ToString());
			builder.AppendLine(ManagerDisposed.ToString());
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
