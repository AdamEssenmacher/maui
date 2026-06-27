#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Foundation;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Graphics;

namespace GradientStopsClearRetentionLeakRepro;

public sealed class MainPage : ContentPage
{
	const int Surfaces = 80;
	const int StopsPerSurface = 2;
	const int PayloadBytes = 1024 * 1024;

	readonly string? _resultsPath;
	readonly Label _status;

	public MainPage(string? resultsPath)
	{
		_resultsPath = resultsPath;
		_status = new Label
		{
			Text = "Running GradientStops.Clear retention leak repro...",
			Margin = 24
		};

		Content = _status;
		Loaded += OnLoaded;
	}

	async void OnLoaded(object? sender, EventArgs e)
	{
		Loaded -= OnLoaded;

		string text;
		try
		{
			var result = await RunScenariosAsync();
			text = result.ToText();
		}
		catch (Exception ex)
		{
			text = "RESULT: FAILED" + Environment.NewLine + ex;
		}

		_status.Text = text;

		if (!string.IsNullOrWhiteSpace(_resultsPath))
			System.IO.File.WriteAllText(_resultsPath, text);

		await Task.Delay(250);
		Process.GetCurrentProcess().Kill();
	}

	static async Task<ReproResult> RunScenariosAsync()
	{
		var control = await RunScenarioAsync("control: remove gradient stops individually", clearStops: false);
		var current = await RunScenarioAsync("current: GradientStops.Clear leaves parent hooks", clearStops: true);

		return new ReproResult(Surfaces, StopsPerSurface, PayloadBytes, control, current);
	}

	static async Task<ScenarioResult> RunScenarioAsync(string name, bool clearStops)
	{
		ForceGc();
		var heapBefore = GC.GetTotalMemory(forceFullCollection: true);

		var retainedSurfaces = new List<Border>(Surfaces);
		var brushReferences = new List<WeakReference<LinearGradientBrush>>(Surfaces);
		var stopReferences = new List<WeakReference<GradientStop>>(Surfaces * StopsPerSurface);
		var payloadReferences = new List<WeakReference<Payload>>(Surfaces * StopsPerSurface);
		var bufferReferences = new List<WeakReference<byte[]>>(Surfaces * StopsPerSurface);

		for (var i = 0; i < Surfaces; i++)
		{
			using (new NSAutoreleasePool())
			{
				var brush = new LinearGradientBrush
				{
					StartPoint = new Point(0, 0),
					EndPoint = new Point(1, 1)
				};
				var surface = new Border
				{
					Background = brush,
					Content = new Label { Text = "Live gradient surface " + i }
				};

				for (var j = 0; j < StopsPerSurface; j++)
				{
					var payload = new Payload((i * StopsPerSurface) + j, PayloadBytes);
					var stop = new GradientStop(j == 0 ? Colors.DeepSkyBlue : Colors.OrangeRed, j / (float)(StopsPerSurface - 1))
					{
						BindingContext = payload
					};

					brush.GradientStops.Add(stop);
					stopReferences.Add(new WeakReference<GradientStop>(stop));
					payloadReferences.Add(new WeakReference<Payload>(payload));
					bufferReferences.Add(new WeakReference<byte[]>(payload.Buffer));

					stop = null!;
					payload = null!;
				}

				if (clearStops)
				{
					brush.GradientStops.Clear();
				}
				else
				{
					while (brush.GradientStops.Count > 0)
						brush.GradientStops.RemoveAt(brush.GradientStops.Count - 1);
				}

				retainedSurfaces.Add(surface);
				brushReferences.Add(new WeakReference<LinearGradientBrush>(brush));

				brush = null!;
				surface = null!;
			}

			if (i % 10 == 0)
				await Task.Yield();
		}

		await Task.Delay(250);
		ForceGc();
		await Task.Delay(250);
		ForceGc();
		var heapAfter = GC.GetTotalMemory(forceFullCollection: true);

		var liveStops = GetAlive(stopReferences);
		var result = new ScenarioResult(
			name,
			retainedSurfaces.Count,
			CountAlive(brushReferences),
			liveStops.Count,
			liveStops.Count(stop => stop.Parent is not null),
			CountAlive(payloadReferences),
			CountAlive(bufferReferences),
			heapBefore,
			heapAfter);

		GC.KeepAlive(retainedSurfaces);
		return result;
	}

	static List<T> GetAlive<T>(IEnumerable<WeakReference<T>> references)
		where T : class
	{
		var alive = new List<T>();

		foreach (var reference in references)
		{
			if (reference.TryGetTarget(out var target))
				alive.Add(target);
		}

		return alive;
	}

	static int CountAlive<T>(IEnumerable<WeakReference<T>> references)
		where T : class
	{
		var count = 0;

		foreach (var reference in references)
		{
			if (reference.TryGetTarget(out _))
				count++;
		}

		return count;
	}

	static void ForceGc()
	{
		for (var i = 0; i < 4; i++)
		{
			GC.Collect();
			GC.WaitForPendingFinalizers();
			GC.Collect();
		}
	}
}

public sealed record ReproResult(
	int Surfaces,
	int StopsPerSurface,
	int PayloadBytes,
	ScenarioResult Control,
	ScenarioResult Current)
{
	public int TotalStops => Surfaces * StopsPerSurface;

	public bool LeakProved =>
		Control.LiveSurfaces == Surfaces &&
		Control.AliveBrushes == Surfaces &&
		Control.AliveStops == 0 &&
		Control.AlivePayloads == 0 &&
		Control.AlivePayloadBuffers == 0 &&
		Current.LiveSurfaces == Surfaces &&
		Current.AliveBrushes == Surfaces &&
		Current.AliveStops == TotalStops &&
		Current.AliveStopsWithParent == TotalStops &&
		Current.AlivePayloads == TotalStops &&
		Current.AlivePayloadBuffers == TotalStops;

	public string ToText()
	{
		return string.Join(Environment.NewLine,
			"GradientStopsClearRetentionLeakRepro",
			$"Live gradient surfaces: {Surfaces}",
			$"Stops per surface: {StopsPerSurface}",
			$"Payload per stop: {PayloadBytes / 1024 / 1024} MiB",
			$"Leak proved: {LeakProved}",
			string.Empty,
			Control.ToText(Surfaces, TotalStops, PayloadBytes),
			string.Empty,
			Current.ToText(Surfaces, TotalStops, PayloadBytes));
	}
}

public sealed record ScenarioResult(
	string Name,
	int LiveSurfaces,
	int AliveBrushes,
	int AliveStops,
	int AliveStopsWithParent,
	int AlivePayloads,
	int AlivePayloadBuffers,
	long HeapBefore,
	long HeapAfter)
{
	public string ToText(int surfaces, int totalStops, int payloadBytes)
	{
		var retainedPayloadBytes = (long)AlivePayloadBuffers * payloadBytes;
		var totalPayloadBytes = (long)totalStops * payloadBytes;

		return string.Join(Environment.NewLine,
			$"Run: {Name}",
			$"  live surfaces intentionally retained: {LiveSurfaces}/{surfaces}",
			$"  live brushes intentionally retained by surfaces: {AliveBrushes}/{surfaces}",
			$"  removed gradient stops alive after full GC: {AliveStops}/{totalStops}",
			$"  removed gradient stops still reporting a parent: {AliveStopsWithParent}/{totalStops}",
			$"  payloads alive after full GC: {AlivePayloads}/{totalStops}",
			$"  payload byte arrays alive after full GC: {AlivePayloadBuffers}/{totalStops}",
			$"  retained payload bytes: {FormatBytes(retainedPayloadBytes)} ({retainedPayloadBytes * 100.0 / totalPayloadBytes:0.0}%)",
			$"  managed heap before: {FormatBytes(HeapBefore)}",
			$"  managed heap after: {FormatBytes(HeapAfter)}",
			$"  managed heap delta: {FormatBytes(HeapAfter - HeapBefore)}");
	}

	static string FormatBytes(long bytes)
	{
		var sign = bytes < 0 ? "-" : "";
		var value = Math.Abs((double)bytes);
		if (value >= 1024 * 1024)
			return $"{sign}{value / 1024 / 1024:0.0} MiB";
		if (value >= 1024)
			return $"{sign}{value / 1024:0.0} KiB";
		return $"{bytes} B";
	}
}

public sealed class Payload
{
	public Payload(int index, int size)
	{
		Buffer = new byte[size];

		for (var i = 0; i < Buffer.Length; i += 4096)
			Buffer[i] = (byte)(index + i);
	}

	public byte[] Buffer { get; }
}
