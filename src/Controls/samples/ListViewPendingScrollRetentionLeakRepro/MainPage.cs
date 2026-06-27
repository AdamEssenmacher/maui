#nullable enable
#pragma warning disable CS0618 // This repro intentionally targets legacy ListView.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Foundation;
using Microsoft.Maui.Controls;

namespace ListViewPendingScrollRetentionLeakRepro;

public sealed class MainPage : ContentPage
{
	const int RequestCount = 80;
	const int PayloadBytes = 1024 * 1024;

	static readonly FieldInfo PendingScrollField =
		typeof(ListView).GetField("_pendingScroll", BindingFlags.Instance | BindingFlags.NonPublic)
		?? throw new MissingFieldException(typeof(ListView).FullName, "_pendingScroll");

	readonly string? _resultsPath;
	readonly Label _status;

	public MainPage(string? resultsPath)
	{
		_resultsPath = resultsPath;
		_status = new Label
		{
			Text = "Running ListView pending ScrollTo retention leak repro...",
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
		var control = await RunScenarioAsync(
			"control: clear pending detached ListView ScrollTo request after item-source removal",
			clearPendingRequest: true);

		var current = await RunScenarioAsync(
			"current: detached ListView keeps pending ScrollToRequestedEventArgs",
			clearPendingRequest: false);

		return new ReproResult(RequestCount, PayloadBytes, control, current);
	}

	static async Task<ScenarioResult> RunScenarioAsync(string name, bool clearPendingRequest)
	{
		ForceGc();
		var heapBefore = GC.GetTotalMemory(forceFullCollection: true);

		var retainedListViews = new List<ListView>(RequestCount);
		var listViewReferences = new List<WeakReference<ListView>>(RequestCount);
		var payloadReferences = new List<WeakReference<Payload>>(RequestCount);
		var bufferReferences = new List<WeakReference<byte[]>>(RequestCount);

		for (var i = 0; i < RequestCount; i++)
		{
			var listView = CreateDetachedListViewWithRemovedScrollItem(i, clearPendingRequest, payloadReferences, bufferReferences);
			retainedListViews.Add(listView);
			listViewReferences.Add(new WeakReference<ListView>(listView));
			listView = null!;

			if (i % 10 == 0)
				await Task.Yield();
		}

		await Task.Delay(250);
		ForceGc();
		await Task.Delay(250);
		ForceGc();
		var heapAfter = GC.GetTotalMemory(forceFullCollection: true);

		var pendingRequests = retainedListViews.Count(static listView => PendingScrollField.GetValue(listView) is not null);

		GC.KeepAlive(retainedListViews);

		return new ScenarioResult(
			name,
			CountAlive(listViewReferences),
			pendingRequests,
			CountAlive(payloadReferences),
			CountAlive(bufferReferences),
			heapBefore,
			heapAfter);
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	static ListView CreateDetachedListViewWithRemovedScrollItem(
		int id,
		bool clearPendingRequest,
		List<WeakReference<Payload>> payloadReferences,
		List<WeakReference<byte[]>> bufferReferences)
	{
		using var pool = new NSAutoreleasePool();
		var payload = new Payload(id, PayloadBytes);
		var items = new List<Payload> { payload };
		var listView = new ListView
		{
			ItemsSource = items
		};

		listView.ScrollTo(payload, ScrollToPosition.MakeVisible, animated: true);
		items.Clear();
		listView.ItemsSource = null;

		if (clearPendingRequest)
			ClearPendingScrollRequest(listView);

		payloadReferences.Add(new WeakReference<Payload>(payload));
		bufferReferences.Add(new WeakReference<byte[]>(payload.Buffer));

		return listView;
	}

	static void ClearPendingScrollRequest(ListView listView)
	{
		PendingScrollField.SetValue(listView, null);
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
	int RequestCount,
	int PayloadBytes,
	ScenarioResult Control,
	ScenarioResult Current)
{
	public bool LeakProved =>
		Control.AliveListViews == RequestCount &&
		Control.PendingScrollRequests == 0 &&
		Control.AlivePayloads == 0 &&
		Control.AlivePayloadBuffers == 0 &&
		Current.AliveListViews == RequestCount &&
		Current.PendingScrollRequests == RequestCount &&
		Current.AlivePayloads == RequestCount &&
		Current.AlivePayloadBuffers == RequestCount;

	public string ToText()
	{
		return string.Join(Environment.NewLine,
			"ListViewPendingScrollRetentionLeakRepro",
			$"Detached ListView instances kept alive: {RequestCount}",
			$"Payload per removed item: {PayloadBytes / 1024 / 1024} MiB",
			$"Leak proved: {LeakProved}",
			string.Empty,
			Control.ToText(RequestCount, PayloadBytes),
			string.Empty,
			Current.ToText(RequestCount, PayloadBytes));
	}
}

public sealed record ScenarioResult(
	string Name,
	int AliveListViews,
	int PendingScrollRequests,
	int AlivePayloads,
	int AlivePayloadBuffers,
	long HeapBefore,
	long HeapAfter)
{
	public string ToText(int requestCount, int payloadBytes)
	{
		var retainedPayloadBytes = (long)AlivePayloadBuffers * payloadBytes;
		var totalPayloadBytes = (long)requestCount * payloadBytes;

		return string.Join(Environment.NewLine,
			$"Run: {Name}",
			$"  list views alive after full GC: {AliveListViews}/{requestCount}",
			$"  pending ScrollToRequestedEventArgs fields: {PendingScrollRequests}/{requestCount}",
			$"  removed item payloads alive after full GC: {AlivePayloads}/{requestCount}",
			$"  payload byte arrays alive after full GC: {AlivePayloadBuffers}/{requestCount}",
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
	public Payload(int id, int bytes)
	{
		Id = id;
		Buffer = new byte[bytes];

		for (var i = 0; i < Buffer.Length; i += 4096)
			Buffer[i] = (byte)(id + i);
	}

	public int Id { get; }

	public byte[] Buffer { get; }

	public override string ToString() => "Payload " + Id;
}
