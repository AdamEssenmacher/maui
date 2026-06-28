#nullable enable

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Controls.Maps;
using Microsoft.Maui.Devices.Sensors;

namespace MapItemsSourceSubscriptionRetentionRepro;

public sealed class MainPage : ContentPage
{
	const int Iterations = 80;
	const int PayloadBytes = 1024 * 1024;

	readonly string? _resultsPath;
	readonly Label _status;

	public MainPage(string? resultsPath)
	{
		_resultsPath = resultsPath;
		_status = new Label
		{
			Text = "Running Map.ItemsSource subscription retention leak repro...",
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
		var control = await RunScenarioAsync("control: clear ItemsSource before dropping maps", clearItemsSourceBeforeDrop: true);
		var current = await RunScenarioAsync("current: shared ItemsSource keeps map CollectionChanged subscribers", clearItemsSourceBeforeDrop: false);

		return new ReproResult(Iterations, PayloadBytes, control, current);
	}

	static async Task<ScenarioResult> RunScenarioAsync(string name, bool clearItemsSourceBeforeDrop)
	{
		ForceGc();
		var heapBefore = GC.GetTotalMemory(forceFullCollection: true);

		var source = new ObservableCollection<SourceItem>
		{
			new("Seattle", 47.6205, -122.3493)
		};
		var mapReferences = new List<WeakReference<Map>>(Iterations);
		var payloadReferences = new List<WeakReference<Payload>>(Iterations);
		var payloadBufferReferences = new List<WeakReference<byte[]>>(Iterations);

		for (var i = 0; i < Iterations; i++)
		{
			CreateDroppedMap(source, i, clearItemsSourceBeforeDrop, mapReferences, payloadReferences, payloadBufferReferences);

			if (i % 10 == 0)
				await Task.Yield();
		}

		ForceGc();
		var heapAfter = GC.GetTotalMemory(forceFullCollection: true);

		var result = new ScenarioResult(
			name,
			source.Count,
			CountAlive(mapReferences),
			CountAlive(payloadReferences),
			CountAlive(payloadBufferReferences),
			heapBefore,
			heapAfter);

		GC.KeepAlive(source);
		return result;
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	static void CreateDroppedMap(
		ObservableCollection<SourceItem> source,
		int index,
		bool clearItemsSourceBeforeDrop,
		List<WeakReference<Map>> mapReferences,
		List<WeakReference<Payload>> payloadReferences,
		List<WeakReference<byte[]>> payloadBufferReferences)
	{
		var payload = new Payload(index, PayloadBytes);
		var map = new Map
		{
			BindingContext = payload,
			ItemTemplate = new DataTemplate(() => new Pin
			{
				Label = "Tracked office",
				Address = "1 Microsoft Way",
				Location = new Location(47.6205, -122.3493)
			}),
			ItemsSource = source
		};

		mapReferences.Add(new WeakReference<Map>(map));
		payloadReferences.Add(new WeakReference<Payload>(payload));
		payloadBufferReferences.Add(new WeakReference<byte[]>(payload.Buffer));

		if (clearItemsSourceBeforeDrop)
			map.ClearValue(Map.ItemsSourceProperty);
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
	int Iterations,
	int PayloadBytes,
	ScenarioResult Control,
	ScenarioResult Current)
{
	public bool LeakProved =>
		Control.AliveMaps == 0 &&
		Control.AlivePayloads == 0 &&
		Control.AlivePayloadBuffers == 0 &&
		Current.AliveMaps == Iterations &&
		Current.AlivePayloads == Iterations &&
		Current.AlivePayloadBuffers == Iterations;

	public string ToText()
	{
		return string.Join(Environment.NewLine,
			"MapItemsSourceSubscriptionRetentionRepro",
			$"Iterations: {Iterations}",
			$"Payload per discarded map: {PayloadBytes / 1024 / 1024} MiB",
			$"Leak proved: {LeakProved}",
			string.Empty,
			Control.ToText(Iterations, PayloadBytes),
			string.Empty,
			Current.ToText(Iterations, PayloadBytes));
	}
}

public sealed record ScenarioResult(
	string Name,
	int SourceItemCount,
	int AliveMaps,
	int AlivePayloads,
	int AlivePayloadBuffers,
	long HeapBefore,
	long HeapAfter)
{
	public string ToText(int iterations, int payloadBytes)
	{
		var retainedPayloadBytes = (long)AlivePayloadBuffers * payloadBytes;

		return string.Join(Environment.NewLine,
			$"Run: {Name}",
			$"  long-lived source items: {SourceItemCount}",
			$"  maps alive after full GC: {AliveMaps}/{iterations}",
			$"  map payloads alive after full GC: {AlivePayloads}/{iterations}",
			$"  payload byte arrays alive after full GC: {AlivePayloadBuffers}/{iterations}",
			$"  retained payload bytes: {FormatBytes(retainedPayloadBytes)} ({retainedPayloadBytes * 100.0 / (payloadBytes * iterations):0.0}%)",
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

public sealed record SourceItem(string Name, double Latitude, double Longitude);
