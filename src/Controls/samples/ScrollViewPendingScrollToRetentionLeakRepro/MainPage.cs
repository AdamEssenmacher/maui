#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Foundation;
using Microsoft.Maui.Controls;

namespace ScrollViewPendingScrollToRetentionLeakRepro;

public sealed class MainPage : ContentPage
{
	const int RequestCount = 80;
	const int PayloadBytes = 1024 * 1024;

	static readonly FieldInfo PendingScrollToRequestedField =
		typeof(ScrollView).GetField("_pendingScrollToRequested", BindingFlags.Instance | BindingFlags.NonPublic)
		?? throw new MissingFieldException(typeof(ScrollView).FullName, "_pendingScrollToRequested");

	static readonly FieldInfo ScrollCompletionSourceField =
		typeof(ScrollView).GetField("_scrollCompletionSource", BindingFlags.Instance | BindingFlags.NonPublic)
		?? throw new MissingFieldException(typeof(ScrollView).FullName, "_scrollCompletionSource");

	readonly string? _resultsPath;
	readonly Label _status;

	public MainPage(string? resultsPath)
	{
		_resultsPath = resultsPath;
		_status = new Label
		{
			Text = "Running ScrollView pending ScrollTo retention leak repro...",
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
			"control: clear pending detached ScrollTo request after content removal",
			clearPendingRequest: true);

		var current = await RunScenarioAsync(
			"current: detached ScrollView keeps pending ScrollToRequestedEventArgs",
			clearPendingRequest: false);

		return new ReproResult(RequestCount, PayloadBytes, control, current);
	}

	static async Task<ScenarioResult> RunScenarioAsync(string name, bool clearPendingRequest)
	{
		ForceGc();
		var heapBefore = GC.GetTotalMemory(forceFullCollection: true);

		var retainedScrollViews = new List<ScrollView>(RequestCount);
		var scrollViewReferences = new List<WeakReference<ScrollView>>(RequestCount);
		var targetReferences = new List<WeakReference<ContentView>>(RequestCount);
		var payloadReferences = new List<WeakReference<Payload>>(RequestCount);
		var bufferReferences = new List<WeakReference<byte[]>>(RequestCount);

		for (var i = 0; i < RequestCount; i++)
		{
			using (new NSAutoreleasePool())
			{
				var payload = new Payload(i, PayloadBytes);
				var target = new ContentView
				{
					BindingContext = payload,
					Content = new Label { Text = "Target " + i }
				};
				var scrollView = new ScrollView
				{
					Content = target
				};

				_ = scrollView.ScrollToAsync(target, ScrollToPosition.MakeVisible, animated: true);

				if (clearPendingRequest)
					ClearPendingScrollRequest(scrollView);

				scrollView.Content = null;

				retainedScrollViews.Add(scrollView);
				scrollViewReferences.Add(new WeakReference<ScrollView>(scrollView));
				targetReferences.Add(new WeakReference<ContentView>(target));
				payloadReferences.Add(new WeakReference<Payload>(payload));
				bufferReferences.Add(new WeakReference<byte[]>(payload.Buffer));

				scrollView = null!;
				target = null!;
				payload = null!;
			}

			if (i % 10 == 0)
				await Task.Yield();
		}

		await Task.Delay(250);
		ForceGc();
		await Task.Delay(250);
		ForceGc();
		var heapAfter = GC.GetTotalMemory(forceFullCollection: true);

		var pendingRequests = retainedScrollViews.Count(static scrollView => PendingScrollToRequestedField.GetValue(scrollView) is not null);
		var pendingTasks = retainedScrollViews.Count(static scrollView => ScrollCompletionSourceField.GetValue(scrollView) is not null);

		GC.KeepAlive(retainedScrollViews);

		return new ScenarioResult(
			name,
			CountAlive(scrollViewReferences),
			pendingRequests,
			pendingTasks,
			CountAlive(targetReferences),
			CountAlive(payloadReferences),
			CountAlive(bufferReferences),
			heapBefore,
			heapAfter);
	}

	static void ClearPendingScrollRequest(ScrollView scrollView)
	{
		if (ScrollCompletionSourceField.GetValue(scrollView) is TaskCompletionSource<bool> completionSource)
			completionSource.TrySetCanceled();

		PendingScrollToRequestedField.SetValue(scrollView, null);
		ScrollCompletionSourceField.SetValue(scrollView, null);
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
		Control.AliveScrollViews == RequestCount &&
		Control.PendingScrollRequests == 0 &&
		Control.AliveTargets == 0 &&
		Control.AlivePayloads == 0 &&
		Control.AlivePayloadBuffers == 0 &&
		Current.AliveScrollViews == RequestCount &&
		Current.PendingScrollRequests == RequestCount &&
		Current.AliveTargets == RequestCount &&
		Current.AlivePayloads == RequestCount &&
		Current.AlivePayloadBuffers == RequestCount;

	public string ToText()
	{
		return string.Join(Environment.NewLine,
			"ScrollViewPendingScrollToRetentionLeakRepro",
			$"Detached ScrollView instances kept alive: {RequestCount}",
			$"Payload per removed target: {PayloadBytes / 1024 / 1024} MiB",
			$"Leak proved: {LeakProved}",
			string.Empty,
			Control.ToText(RequestCount, PayloadBytes),
			string.Empty,
			Current.ToText(RequestCount, PayloadBytes));
	}
}

public sealed record ScenarioResult(
	string Name,
	int AliveScrollViews,
	int PendingScrollRequests,
	int PendingScrollTasks,
	int AliveTargets,
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
			$"  scroll views alive after full GC: {AliveScrollViews}/{requestCount}",
			$"  pending ScrollToRequestedEventArgs fields: {PendingScrollRequests}/{requestCount}",
			$"  pending scroll TaskCompletionSource fields: {PendingScrollTasks}/{requestCount}",
			$"  removed targets alive after full GC: {AliveTargets}/{requestCount}",
			$"  payloads alive after full GC: {AlivePayloads}/{requestCount}",
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
	public Payload(int index, int size)
	{
		Buffer = new byte[size];

		for (var i = 0; i < Buffer.Length; i += 4096)
			Buffer[i] = (byte)(index + i);
	}

	public byte[] Buffer { get; }
}
