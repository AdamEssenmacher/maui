#nullable enable

using System.Collections;
using System.Reflection;
using System.Text;
using Microsoft.Maui.Animations;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Storage;

namespace AnimationExtensionsLeakRepro;

public sealed class MainPage : ContentPage
{
	const int Iterations = 80;
	const int PayloadBytes = 1024 * 1024;

	readonly Label _status;

	public MainPage()
	{
		_status = new Label
		{
			Text = "Running AnimationExtensions leak repro...",
			Margin = 24
		};

		Content = _status;
		Loaded += OnLoaded;
	}

	async void OnLoaded(object? sender, EventArgs e)
	{
		Loaded -= OnLoaded;

		ReproResult result;

		try
		{
			result = await RunScenariosAsync();
		}
		catch (Exception ex)
		{
			var failure = "RESULT: ERROR" + Environment.NewLine + ex;
			_status.Text = failure;
			await WriteResultsAsync(failure);
			await Task.Delay(250);
			Environment.Exit(3);
			return;
		}

		var text = result.ToString();
		_status.Text = text;
		await WriteResultsAsync(text);
		await Task.Delay(250);
		Environment.Exit(result.IsProven ? 0 : 2);
	}

	static async Task<ReproResult> RunScenariosAsync()
	{
		var before = StaticCounts.Read();
		var control = await RunOwnerAliveControlAsync();
		var leak = await RunOwnerCollectedBeforeFinishAsync();
		var after = StaticCounts.Read();

		return new ReproResult(before, control, leak, after);
	}

	static async Task<ScenarioResult> RunOwnerAliveControlAsync()
	{
		var payloadRefs = new List<WeakReference>();
		var probeRefs = new List<WeakReference>();
		var managerRefs = new List<WeakReference>();
		var tickerRefs = new List<WeakReference>();
		var probes = new List<ProbeAnimatable>();
		var tickers = new List<ManualTicker>();

		await MainThread.InvokeOnMainThreadAsync(() =>
		{
			for (var i = 0; i < Iterations; i++)
			{
				CreateAnimation($"control-{i}", payloadRefs, probeRefs, managerRefs, tickerRefs, probes, tickers);
			}
		});

		await FinishAnimationsAsync(tickers);
		var probesAliveBeforeRelease = CountAlive(probeRefs);

		probes.Clear();
		tickers.Clear();

		await WaitAndCollectAsync();

		return new ScenarioResult(
			"owner-alive-control",
			CountAlive(payloadRefs),
			CountAlive(probeRefs),
			CountAlive(managerRefs),
			CountAlive(tickerRefs),
			Iterations,
			probesAliveBeforeRelease,
			StaticCounts.Read());
	}

	static async Task<ScenarioResult> RunOwnerCollectedBeforeFinishAsync()
	{
		var payloadRefs = new List<WeakReference>();
		var probeRefs = new List<WeakReference>();
		var managerRefs = new List<WeakReference>();
		var tickerRefs = new List<WeakReference>();
		var tickers = new List<ManualTicker>();

		await MainThread.InvokeOnMainThreadAsync(() =>
		{
			for (var i = 0; i < Iterations; i++)
			{
				CreateAnimation($"leak-{i}", payloadRefs, probeRefs, managerRefs, tickerRefs, null, tickers);
			}
		});

		await WaitAndCollectAsync();
		var probesAliveBeforeFinish = CountAlive(probeRefs);

		await FinishAnimationsAsync(tickers);
		tickers.Clear();

		await WaitAndCollectAsync();

		return new ScenarioResult(
			"owner-collected-before-finish",
			CountAlive(payloadRefs),
			CountAlive(probeRefs),
			CountAlive(managerRefs),
			CountAlive(tickerRefs),
			Iterations,
			probesAliveBeforeFinish,
			StaticCounts.Read());
	}

	static void CreateAnimation(
		string name,
		List<WeakReference> payloadRefs,
		List<WeakReference> probeRefs,
		List<WeakReference> managerRefs,
		List<WeakReference> tickerRefs,
		List<ProbeAnimatable>? strongProbes,
		List<ManualTicker> strongTickers)
	{
		var payload = new Payload(PayloadBytes);
		var probe = new ProbeAnimatable();
		var ticker = new ManualTicker();
		var manager = new AnimationManager(ticker)
		{
			SpeedModifier = 10_000
		};

		probe.Animate(name, static value => value, _ => payload.Touch(), rate: 16, length: 250, animationManager: manager);

		payloadRefs.Add(new WeakReference(payload));
		probeRefs.Add(new WeakReference(probe));
		managerRefs.Add(new WeakReference(manager));
		tickerRefs.Add(new WeakReference(ticker));
		strongProbes?.Add(probe);
		strongTickers.Add(ticker);
	}

	static async Task FinishAnimationsAsync(List<ManualTicker> tickers)
	{
		for (var i = 0; i < 2; i++)
		{
			await Task.Delay(50);
			foreach (var ticker in tickers)
			{
				ticker.Pulse();
			}
		}
	}

	static async Task WaitAndCollectAsync()
	{
		await Task.Delay(250);
		await Task.Run(ForceGc);
		await Task.Delay(100);
		await Task.Run(ForceGc);
	}

	static void ForceGc()
	{
		for (var i = 0; i < 4; i++)
		{
			GC.Collect();
			GC.WaitForPendingFinalizers();
			GC.Collect();
			Thread.Sleep(100);
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

	static async Task WriteResultsAsync(string text)
	{
		var paths = new[]
		{
			Path.Combine(FileSystem.AppDataDirectory, "autorun-results.txt"),
			Path.Combine(Path.GetTempPath(), "animationextensionsleakrepro-results.txt")
		};

		foreach (var path in paths)
		{
			try
			{
				var directory = Path.GetDirectoryName(path);
				if (!string.IsNullOrWhiteSpace(directory))
					Directory.CreateDirectory(directory);

				await File.WriteAllTextAsync(path, text);
			}
			catch (Exception ex)
			{
				Console.WriteLine(ex);
			}
		}

		Console.WriteLine(text);
	}

	sealed class ManualTicker : ITicker
	{
		public bool IsRunning { get; private set; }

		public bool SystemEnabled => true;

		public int MaxFps { get; set; } = 60;

		public Action? Fire { get; set; }

		public void Start() => IsRunning = true;

		public void Stop() => IsRunning = false;

		public void Pulse()
		{
			if (IsRunning)
				Fire?.Invoke();
		}
	}

	sealed class ProbeAnimatable : IAnimatable
	{
		public void BatchBegin()
		{
		}

		public void BatchCommit()
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

	readonly record struct StaticCounts(int Animations, int Tweeners)
	{
		public static StaticCounts Read()
		{
			return new StaticCounts(ReadDictionaryCount("s_animations"), ReadDictionaryCount("s_tweeners"));
		}

		static int ReadDictionaryCount(string fieldName)
		{
			var field = typeof(AnimationExtensions).GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Static)
				?? throw new InvalidOperationException($"Missing field {fieldName}.");
			var dictionary = (ICollection?)field.GetValue(null)
				?? throw new InvalidOperationException($"Field {fieldName} was null.");
			return dictionary.Count;
		}

		public override string ToString() => $"static-animations={Animations}, static-tweeners={Tweeners}";
	}

	readonly record struct ScenarioResult(
		string Name,
		int PayloadsAlive,
		int ProbesAlive,
		int ManagersAlive,
		int TickersAlive,
		int Total,
		int ProbesAliveBeforeFinish,
		StaticCounts StaticCounts)
	{
		public override string ToString()
		{
			var builder = new StringBuilder();
			builder.Append(Name);
			builder.Append(": payloads=");
			builder.Append(PayloadsAlive);
			builder.Append('/');
			builder.Append(Total);
			builder.Append(", probes=");
			builder.Append(ProbesAlive);
			builder.Append('/');
			builder.Append(Total);
			builder.Append(", managers=");
			builder.Append(ManagersAlive);
			builder.Append('/');
			builder.Append(Total);
			builder.Append(", tickers=");
			builder.Append(TickersAlive);
			builder.Append('/');
			builder.Append(Total);
			builder.Append(", probes-before-finish=");
			builder.Append(ProbesAliveBeforeFinish);
			builder.Append('/');
			builder.Append(Total);
			builder.Append(", ");
			builder.Append(StaticCounts);
			return builder.ToString();
		}
	}

	readonly record struct ReproResult(StaticCounts Before, ScenarioResult Control, ScenarioResult Leak, StaticCounts After)
	{
		static int TotalLeakThreshold => Iterations / 2;

		public bool IsProven =>
			Before.Animations == 0 &&
			Control.PayloadsAlive == 0 &&
			Control.ManagersAlive == 0 &&
			Control.StaticCounts.Animations == 0 &&
			Leak.ProbesAliveBeforeFinish == 0 &&
			Leak.PayloadsAlive >= TotalLeakThreshold &&
			Leak.ManagersAlive >= TotalLeakThreshold &&
			Leak.TickersAlive >= TotalLeakThreshold &&
			Leak.StaticCounts.Animations >= TotalLeakThreshold &&
			After.Tweeners == 0;

		public override string ToString()
		{
			var builder = new StringBuilder();
			builder.AppendLine(IsProven ? "RESULT: PROVEN" : "RESULT: NOT PROVEN");
			builder.AppendLine("before: " + Before);
			builder.AppendLine(Control.ToString());
			builder.AppendLine(Leak.ToString());
			builder.AppendLine("after: " + After);
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
