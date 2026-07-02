#nullable enable
#pragma warning disable CS0618 // This repro intentionally targets a legacy compatibility renderer.

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
using ControlsContentView = Microsoft.Maui.Controls.ContentView;
using LegacyPlatform = Microsoft.Maui.Controls.Compatibility.Platform.iOS.Platform;
using LegacyScrollViewRenderer = Microsoft.Maui.Controls.Compatibility.Platform.iOS.ScrollViewRenderer;

namespace ScrollViewRendererPendingScrollRetentionLeakRepro;

public sealed class MainPage : ContentPage
{
	const int RequestCount = 80;
	const int PayloadBytes = 1024 * 1024;

	static readonly FieldInfo RendererRequestedScrollField =
		typeof(LegacyScrollViewRenderer).GetField("_requestedScroll", BindingFlags.Instance | BindingFlags.NonPublic)
		?? throw new MissingFieldException(typeof(LegacyScrollViewRenderer).FullName, "_requestedScroll");

	static readonly FieldInfo CorePendingScrollToRequestedField =
		typeof(ScrollView).GetField("_pendingScrollToRequested", BindingFlags.Instance | BindingFlags.NonPublic)
		?? throw new MissingFieldException(typeof(ScrollView).FullName, "_pendingScrollToRequested");

	static readonly FieldInfo CoreScrollCompletionSourceField =
		typeof(ScrollView).GetField("_scrollCompletionSource", BindingFlags.Instance | BindingFlags.NonPublic)
		?? throw new MissingFieldException(typeof(ScrollView).FullName, "_scrollCompletionSource");

	readonly string? _resultsPath;
	readonly Label _status;

	public MainPage(string? resultsPath)
	{
		_resultsPath = resultsPath;
		_status = new Label
		{
			Text = "Running ScrollViewRenderer pending ScrollTo retention leak repro...",
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
			"control: clear legacy renderer pending ScrollTo request after content removal",
			clearRendererRequest: true);

		var current = await RunScenarioAsync(
			mauiContext,
			"current: offscreen ScrollViewRenderer keeps pending ScrollToRequestedEventArgs",
			clearRendererRequest: false);

		return new ReproResult(RequestCount, PayloadBytes, control, current);
	}

	static async Task<ScenarioResult> RunScenarioAsync(IMauiContext mauiContext, string name, bool clearRendererRequest)
	{
		ForceGc();
		var heapBefore = GC.GetTotalMemory(forceFullCollection: true);

		var retainedRenderers = new List<LegacyScrollViewRenderer>(RequestCount);
		var retainedScrollViews = new List<ScrollView>(RequestCount);
		var rendererReferences = new List<WeakReference<LegacyScrollViewRenderer>>(RequestCount);
		var scrollViewReferences = new List<WeakReference<ScrollView>>(RequestCount);
		var modernHandlerReferences = new List<WeakReference<IElementHandler>>(RequestCount);
		var targetReferences = new List<WeakReference<ControlsContentView>>(RequestCount);
		var payloadReferences = new List<WeakReference<Payload>>(RequestCount);
		var bufferReferences = new List<WeakReference<byte[]>>(RequestCount);
		var modernHandlerTypeNames = new HashSet<string>(StringComparer.Ordinal);

		for (var i = 0; i < RequestCount; i++)
		{
			var pair = CreateOffscreenRendererWithRemovedScrollTarget(
				mauiContext,
				i,
				clearRendererRequest,
				modernHandlerReferences,
				targetReferences,
				payloadReferences,
				bufferReferences);

			retainedRenderers.Add(pair.Renderer);
			retainedScrollViews.Add(pair.ScrollView);
			rendererReferences.Add(new WeakReference<LegacyScrollViewRenderer>(pair.Renderer));
			scrollViewReferences.Add(new WeakReference<ScrollView>(pair.ScrollView));
			modernHandlerTypeNames.Add(pair.ModernHandlerTypeName);
			pair = default;

			if (i % 10 == 0)
				await Task.Yield();
		}

		await Task.Delay(250);
		ForceGc();
		await Task.Delay(250);
		ForceGc();
		var heapAfter = GC.GetTotalMemory(forceFullCollection: true);

		var rendererPendingRequests = retainedRenderers.Count(static renderer => RendererRequestedScrollField.GetValue(renderer) is not null);
		var corePendingRequests = retainedScrollViews.Count(static scrollView => CorePendingScrollToRequestedField.GetValue(scrollView) is not null);
		var corePendingTasks = retainedScrollViews.Count(static scrollView => CoreScrollCompletionSourceField.GetValue(scrollView) is not null);

		GC.KeepAlive(retainedRenderers);
		GC.KeepAlive(retainedScrollViews);

		return new ScenarioResult(
			name,
			string.Join(", ", modernHandlerTypeNames.OrderBy(static name => name)),
			CountAlive(rendererReferences),
			CountAlive(scrollViewReferences),
			CountAlive(modernHandlerReferences),
			rendererPendingRequests,
			corePendingRequests,
			corePendingTasks,
			CountAlive(targetReferences),
			CountAlive(payloadReferences),
			CountAlive(bufferReferences),
			heapBefore,
			heapAfter);
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	static (ScrollView ScrollView, LegacyScrollViewRenderer Renderer, string ModernHandlerTypeName) CreateOffscreenRendererWithRemovedScrollTarget(
		IMauiContext mauiContext,
		int id,
		bool clearRendererRequest,
		List<WeakReference<IElementHandler>> modernHandlerReferences,
		List<WeakReference<ControlsContentView>> targetReferences,
		List<WeakReference<Payload>> payloadReferences,
		List<WeakReference<byte[]>> bufferReferences)
	{
		using var pool = new NSAutoreleasePool();
		var payload = new Payload(id, PayloadBytes);
		var target = new ControlsContentView
		{
			BindingContext = payload,
			Content = new Label { Text = "Target " + id }
		};
		var scrollView = new ScrollView
		{
			Content = target,
			HeightRequest = 200,
			WidthRequest = 320
		};

		var modernHandler = scrollView.ToHandler(mauiContext);
		var modernHandlerTypeName = modernHandler.GetType().FullName ?? modernHandler.GetType().Name;
		var renderer = new LegacyScrollViewRenderer();
		LegacyPlatform.SetRenderer(scrollView, renderer);
		renderer.SetElement(scrollView);

		_ = scrollView.ScrollToAsync(target, ScrollToPosition.MakeVisible, animated: true);
		scrollView.Content = null;
		CancelCoreScrollTask(scrollView);

		if (clearRendererRequest)
			RendererRequestedScrollField.SetValue(renderer, null);

		scrollView.Handler = null;

		modernHandlerReferences.Add(new WeakReference<IElementHandler>(modernHandler));
		targetReferences.Add(new WeakReference<ControlsContentView>(target));
		payloadReferences.Add(new WeakReference<Payload>(payload));
		bufferReferences.Add(new WeakReference<byte[]>(payload.Buffer));

		modernHandler = null!;
		target = null!;
		payload = null!;

		return (scrollView, renderer, modernHandlerTypeName);
	}

	static void CancelCoreScrollTask(ScrollView scrollView)
	{
		if (CoreScrollCompletionSourceField.GetValue(scrollView) is TaskCompletionSource<bool> completionSource)
			completionSource.TrySetCanceled();

		CoreScrollCompletionSourceField.SetValue(scrollView, null);
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
		Control.AliveRenderers == RequestCount &&
		Control.AliveScrollViews == RequestCount &&
		Control.RendererPendingScrollRequests == 0 &&
		Control.CorePendingScrollRequests == 0 &&
		Control.AliveTargets == 0 &&
		Control.AlivePayloads == 0 &&
		Control.AlivePayloadBuffers == 0 &&
		Current.AliveRenderers == RequestCount &&
		Current.AliveScrollViews == RequestCount &&
		Current.RendererPendingScrollRequests == RequestCount &&
		Current.CorePendingScrollRequests == 0 &&
		Current.AliveTargets == RequestCount &&
		Current.AlivePayloads == RequestCount &&
		Current.AlivePayloadBuffers == RequestCount;

	public string ToText()
	{
		return string.Join(Environment.NewLine,
			"ScrollViewRendererPendingScrollRetentionLeakRepro",
			$"Offscreen legacy ScrollViewRenderer instances kept alive: {RequestCount}",
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
	string ModernHandlerTypes,
	int AliveRenderers,
	int AliveScrollViews,
	int AliveModernHandlers,
	int RendererPendingScrollRequests,
	int CorePendingScrollRequests,
	int CorePendingScrollTasks,
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
			$"  modern handler types: {ModernHandlerTypes}",
			$"  legacy renderers alive after full GC: {AliveRenderers}/{requestCount}",
			$"  scroll views alive after full GC: {AliveScrollViews}/{requestCount}",
			$"  modern handlers alive after full GC: {AliveModernHandlers}/{requestCount}",
			$"  legacy renderer _requestedScroll fields: {RendererPendingScrollRequests}/{requestCount}",
			$"  core ScrollView _pendingScrollToRequested fields: {CorePendingScrollRequests}/{requestCount}",
			$"  core ScrollView _scrollCompletionSource fields: {CorePendingScrollTasks}/{requestCount}",
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
