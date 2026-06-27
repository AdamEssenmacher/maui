#nullable enable
#pragma warning disable CS0618 // This repro intentionally targets legacy ListView and its compatibility renderer.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Foundation;
using Microsoft.Maui;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Platform;

namespace ListViewRendererPendingScrollRetentionLeakRepro;

public sealed class MainPage : ContentPage
{
	const int RequestCount = 80;
	const int PayloadBytes = 1024 * 1024;

	static readonly FieldInfo CorePendingScrollField =
		typeof(ListView).GetField("_pendingScroll", BindingFlags.Instance | BindingFlags.NonPublic)
		?? throw new MissingFieldException(typeof(ListView).FullName, "_pendingScroll");

	readonly string? _resultsPath;
	readonly Label _status;

	public MainPage(string? resultsPath)
	{
		_resultsPath = resultsPath;
		_status = new Label
		{
			Text = "Running ListViewRenderer pending ScrollTo retention leak repro...",
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
			var mauiContext = Handler?.MauiContext
				?? throw new InvalidOperationException("MainPage handler has no MauiContext.");

			var result = await RunScenariosAsync(mauiContext);
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

	static async Task<ReproResult> RunScenariosAsync(IMauiContext mauiContext)
	{
		var control = await RunScenarioAsync(
			mauiContext,
			"control: clear renderer pending ScrollTo request after item-source removal",
			clearRendererRequest: true);

		var current = await RunScenarioAsync(
			mauiContext,
			"current: offscreen ListViewRenderer keeps pending ScrollToRequestedEventArgs",
			clearRendererRequest: false);

		return new ReproResult(RequestCount, PayloadBytes, control, current);
	}

	static async Task<ScenarioResult> RunScenarioAsync(IMauiContext mauiContext, string name, bool clearRendererRequest)
	{
		ForceGc();
		var heapBefore = GC.GetTotalMemory(forceFullCollection: true);

		var retainedHandlers = new List<IElementHandler>(RequestCount);
		var retainedListViews = new List<ListView>(RequestCount);
		var handlerReferences = new List<WeakReference<IElementHandler>>(RequestCount);
		var payloadReferences = new List<WeakReference<Payload>>(RequestCount);
		var bufferReferences = new List<WeakReference<byte[]>>(RequestCount);
		var rendererTypeNames = new HashSet<string>(StringComparer.Ordinal);

		for (var i = 0; i < RequestCount; i++)
		{
			var pair = CreateOffscreenRendererWithRemovedScrollItem(
				mauiContext,
				i,
				clearRendererRequest,
				payloadReferences,
				bufferReferences);

			retainedHandlers.Add(pair.Handler);
			retainedListViews.Add(pair.ListView);
			handlerReferences.Add(new WeakReference<IElementHandler>(pair.Handler));
			rendererTypeNames.Add(pair.Handler.GetType().FullName ?? pair.Handler.GetType().Name);
			pair = default;

			if (i % 10 == 0)
				await Task.Yield();
		}

		await Task.Delay(250);
		ForceGc();
		await Task.Delay(250);
		ForceGc();
		var heapAfter = GC.GetTotalMemory(forceFullCollection: true);

		var rendererPendingRequests = retainedHandlers.Count(static handler => GetRendererRequestedScrollField(handler).GetValue(handler) is not null);
		var corePendingRequests = retainedListViews.Count(static listView => CorePendingScrollField.GetValue(listView) is not null);

		GC.KeepAlive(retainedHandlers);
		GC.KeepAlive(retainedListViews);

		return new ScenarioResult(
			name,
			string.Join(", ", rendererTypeNames.OrderBy(static name => name)),
			CountAlive(handlerReferences),
			CountAlive(payloadReferences),
			CountAlive(bufferReferences),
			rendererPendingRequests,
			corePendingRequests,
			heapBefore,
			heapAfter);
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	static (ListView ListView, IElementHandler Handler) CreateOffscreenRendererWithRemovedScrollItem(
		IMauiContext mauiContext,
		int id,
		bool clearRendererRequest,
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

		var handler = listView.ToHandler(mauiContext);
		var requestedScrollField = GetRendererRequestedScrollField(handler);

		listView.ScrollTo(payload, ScrollToPosition.MakeVisible, animated: true);
		items.Clear();
		listView.ItemsSource = null;

		if (clearRendererRequest)
			requestedScrollField.SetValue(handler, null);

		payloadReferences.Add(new WeakReference<Payload>(payload));
		bufferReferences.Add(new WeakReference<byte[]>(payload.Buffer));

		return (listView, handler);
	}

	static FieldInfo GetRendererRequestedScrollField(IElementHandler handler)
	{
		var type = handler.GetType();
		while (type is not null)
		{
			var field = type.GetField("_requestedScroll", BindingFlags.Instance | BindingFlags.NonPublic);
			if (field is not null)
				return field;

			type = type.BaseType;
		}

		throw new MissingFieldException(handler.GetType().FullName, "_requestedScroll");
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
		Control.AliveHandlers == RequestCount &&
		Control.RendererPendingScrollRequests == 0 &&
		Control.CorePendingScrollRequests == 0 &&
		Control.AlivePayloads == 0 &&
		Control.AlivePayloadBuffers == 0 &&
		Current.AliveHandlers == RequestCount &&
		Current.RendererPendingScrollRequests == RequestCount &&
		Current.CorePendingScrollRequests == 0 &&
		Current.AlivePayloads == RequestCount &&
		Current.AlivePayloadBuffers == RequestCount;

	public string ToText()
	{
		return string.Join(Environment.NewLine,
			"ListViewRendererPendingScrollRetentionLeakRepro",
			$"Offscreen ListViewRenderer instances kept alive: {RequestCount}",
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
	string HandlerTypes,
	int AliveHandlers,
	int AlivePayloads,
	int AlivePayloadBuffers,
	int RendererPendingScrollRequests,
	int CorePendingScrollRequests,
	long HeapBefore,
	long HeapAfter)
{
	public string ToText(int requestCount, int payloadBytes)
	{
		var retainedPayloadBytes = (long)AlivePayloadBuffers * payloadBytes;
		var totalPayloadBytes = (long)requestCount * payloadBytes;

		return string.Join(Environment.NewLine,
			$"Run: {Name}",
			$"  handler types: {HandlerTypes}",
			$"  handlers alive after full GC: {AliveHandlers}/{requestCount}",
			$"  renderer _requestedScroll fields: {RendererPendingScrollRequests}/{requestCount}",
			$"  core ListView _pendingScroll fields: {CorePendingScrollRequests}/{requestCount}",
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
