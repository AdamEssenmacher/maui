#nullable enable
#pragma warning disable CS0618 // This repro intentionally targets legacy ListView and its compatibility renderer.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Android.Content;
using Microsoft.Maui;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Controls.Handlers.Compatibility;

namespace AndroidListViewRendererPendingScrollRetentionLeakRepro;

public sealed record RunStats(
	string Name,
	int Attempts,
	int AliveRenderers,
	int AliveListViews,
	int RendererPendingScrollRequests,
	int CorePendingScrollRequests,
	int AlivePayloads,
	int AlivePayloadBuffers,
	long HeapBefore,
	long HeapAfter);

public sealed record ReproReport(
	int Attempts,
	int PayloadBytes,
	RunStats Control,
	RunStats Current)
{
	public bool LeakProved =>
		Control.AliveRenderers == Attempts &&
		Control.RendererPendingScrollRequests == 0 &&
		Control.CorePendingScrollRequests == 0 &&
		Control.AlivePayloads == 0 &&
		Control.AlivePayloadBuffers == 0 &&
		Current.AliveRenderers == Attempts &&
		Current.RendererPendingScrollRequests == Attempts &&
		Current.CorePendingScrollRequests == 0 &&
		Current.AlivePayloads == Attempts &&
		Current.AlivePayloadBuffers == Attempts;

	public string ToText()
	{
		return string.Join(Environment.NewLine,
			"AndroidListViewRendererPendingScrollRetentionLeakRepro",
			$"Offscreen ListViewRenderer instances kept alive: {Attempts}",
			$"Payload per removed item: {PayloadBytes / 1024 / 1024} MiB",
			$"Leak proved: {LeakProved}",
			string.Empty,
			Format(Control),
			string.Empty,
			Format(Current));
	}

	string Format(RunStats stats)
	{
		var retainedPayloadBytes = (long)stats.AlivePayloadBuffers * PayloadBytes;
		var totalPayloadBytes = (long)stats.Attempts * PayloadBytes;

		return string.Join(Environment.NewLine,
			$"Run: {stats.Name}",
			$"  renderers alive after full GC: {stats.AliveRenderers}/{stats.Attempts}",
			$"  ListViews alive after full GC: {stats.AliveListViews}/{stats.Attempts}",
			$"  renderer _pendingScrollTo fields: {stats.RendererPendingScrollRequests}/{stats.Attempts}",
			$"  core ListView _pendingScroll fields: {stats.CorePendingScrollRequests}/{stats.Attempts}",
			$"  removed item payloads alive after full GC: {stats.AlivePayloads}/{stats.Attempts}",
			$"  payload byte arrays alive after full GC: {stats.AlivePayloadBuffers}/{stats.Attempts}",
			$"  retained payload bytes: {FormatBytes(retainedPayloadBytes)} ({retainedPayloadBytes * 100.0 / totalPayloadBytes:0.0}%)",
			$"  managed heap before: {FormatBytes(stats.HeapBefore)}",
			$"  managed heap after: {FormatBytes(stats.HeapAfter)}",
			$"  managed heap delta: {FormatBytes(stats.HeapAfter - stats.HeapBefore)}");
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

internal static class ReproSession
{
	const int Attempts = 80;
	const int PayloadBytes = 1024 * 1024;

	static readonly FieldInfo RendererPendingScrollField =
		typeof(ListViewRenderer).GetField("_pendingScrollTo", BindingFlags.Instance | BindingFlags.NonPublic)
		?? throw new MissingFieldException(nameof(ListViewRenderer), "_pendingScrollTo");

	static readonly FieldInfo CorePendingScrollField =
		typeof(ListView).GetField("_pendingScroll", BindingFlags.Instance | BindingFlags.NonPublic)
		?? throw new MissingFieldException(nameof(ListView), "_pendingScroll");

	public static async Task<ReproReport> RunAsync(IMauiContext mauiContext)
	{
		await Task.Yield();

		var control = await RunScenarioAsync(
			mauiContext,
			"control: clear renderer pending ScrollTo request after item-source removal",
			clearRendererRequest: true);

		var current = await RunScenarioAsync(
			mauiContext,
			"current: offscreen Android ListViewRenderer keeps pending ScrollToRequestedEventArgs",
			clearRendererRequest: false);

		return new ReproReport(Attempts, PayloadBytes, control, current);
	}

	static async Task<RunStats> RunScenarioAsync(IMauiContext mauiContext, string name, bool clearRendererRequest)
	{
		ForceFullGc();
		var heapBefore = GC.GetTotalMemory(forceFullCollection: true);

		var retainedRenderers = new List<ListViewRenderer>(Attempts);
		var retainedListViews = new List<ListView>(Attempts);
		var rendererRefs = new List<WeakReference<ListViewRenderer>>(Attempts);
		var listViewRefs = new List<WeakReference<ListView>>(Attempts);
		var payloadRefs = new List<WeakReference<Payload>>(Attempts);
		var bufferRefs = new List<WeakReference<byte[]>>(Attempts);

		for (var i = 0; i < Attempts; i++)
		{
			var pair = CreateOffscreenRendererWithRemovedScrollItem(
				mauiContext,
				clearRendererRequest,
				payloadRefs,
				bufferRefs,
				i);

			retainedRenderers.Add(pair.Renderer);
			retainedListViews.Add(pair.ListView);
			rendererRefs.Add(new WeakReference<ListViewRenderer>(pair.Renderer));
			listViewRefs.Add(new WeakReference<ListView>(pair.ListView));
			pair = default;

			if (i % 10 == 0)
				await Task.Yield();
		}

		ForceFullGc();
		await Task.Delay(250);
		ForceFullGc();
		var heapAfter = GC.GetTotalMemory(forceFullCollection: true);

		var rendererPendingRequests = retainedRenderers.Count(static renderer => RendererPendingScrollField.GetValue(renderer) is not null);
		var corePendingRequests = retainedListViews.Count(static listView => CorePendingScrollField.GetValue(listView) is not null);

		GC.KeepAlive(retainedRenderers);
		GC.KeepAlive(retainedListViews);

		return new RunStats(
			name,
			Attempts,
			CountAlive(rendererRefs),
			CountAlive(listViewRefs),
			rendererPendingRequests,
			corePendingRequests,
			CountAlive(payloadRefs),
			CountAlive(bufferRefs),
			heapBefore,
			heapAfter);
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	static (ListView ListView, ListViewRenderer Renderer) CreateOffscreenRendererWithRemovedScrollItem(
		IMauiContext mauiContext,
		bool clearRendererRequest,
		List<WeakReference<Payload>> payloadRefs,
		List<WeakReference<byte[]>> bufferRefs,
		int index)
	{
		var payload = new Payload(index, PayloadBytes);
		var items = new List<Payload> { payload };
		var listView = new ListView(ListViewCachingStrategy.RecycleElement)
		{
			ItemsSource = items,
			ItemTemplate = new DataTemplate(static () =>
			{
				var cell = new TextCell();
				cell.SetBinding(TextCell.TextProperty, nameof(Payload.Title));
				return cell;
			})
		};

		var context = mauiContext.Context as Context
			?? throw new InvalidOperationException("Android context is not available.");
		var renderer = new ListViewRenderer(context);
		((IElementHandler)renderer).SetMauiContext(mauiContext);
		((IElementHandler)renderer).SetVirtualView(listView);

		listView.ScrollTo(payload, ScrollToPosition.MakeVisible, animated: true);
		items.Clear();
		listView.ItemsSource = null;

		if (clearRendererRequest)
			RendererPendingScrollField.SetValue(renderer, null);

		payloadRefs.Add(new WeakReference<Payload>(payload));
		bufferRefs.Add(new WeakReference<byte[]>(payload.Bytes));

		return (listView, renderer);
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

	static void ForceFullGc()
	{
		for (var i = 0; i < 4; i++)
		{
			GC.Collect();
			GC.WaitForPendingFinalizers();
			GC.Collect();
		}
	}

	sealed class Payload
	{
		public Payload(int id, int byteCount)
		{
			Id = id;
			Title = "Invoice batch " + id;
			Bytes = new byte[byteCount];

			for (var i = 0; i < Bytes.Length; i += 4096)
				Bytes[i] = (byte)(id + i);
		}

		public int Id { get; }

		public string Title { get; }

		public byte[] Bytes { get; }

		public override string ToString() => Title;
	}
}
