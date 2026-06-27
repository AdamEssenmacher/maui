#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Android.Content;
using AndroidX.SwipeRefreshLayout.Widget;
using Microsoft.Maui;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Controls.Handlers.Compatibility;
using Microsoft.Maui.Controls.Platform;
using Microsoft.Maui.Handlers;
using AListView = Android.Widget.ListView;

namespace AndroidListViewRendererRefreshListenerRetentionLeakRepro;

public sealed record RunStats(
	string Name,
	int Attempts,
	int AliveRenderers,
	int AliveAdapters,
	int AliveListViews,
	int AliveItemSources,
	int AlivePayloads,
	long RetainedPayloadBytes);

public sealed record ReproReport(
	int Attempts,
	int PayloadBytes,
	RunStats Control,
	RunStats Current,
	long ManagedHeapBaseline,
	long ManagedHeapFinal)
{
	public bool LeakProved =>
		Control.AliveListViews == 0 &&
		Control.AliveItemSources == 0 &&
		Control.AlivePayloads == 0 &&
		Current.AliveListViews == Attempts &&
		Current.AliveItemSources == Attempts &&
		Current.AlivePayloads == Attempts;

	public string ToText()
	{
		return string.Join(Environment.NewLine,
			"AndroidListViewRendererRefreshListenerRetentionLeakRepro",
			$"Attempts: {Attempts}",
			$"Payload per attempt: {PayloadBytes / 1024 / 1024} MiB",
			$"Leak proved: {LeakProved}",
			string.Empty,
			Format(Control),
			string.Empty,
			Format(Current),
			string.Empty,
			$"Managed heap baseline: {FormatBytes(ManagedHeapBaseline)}",
			$"Managed heap final: {FormatBytes(ManagedHeapFinal)}",
			$"Managed heap delta: {FormatBytes(ManagedHeapFinal - ManagedHeapBaseline)}");
	}

	string Format(RunStats stats)
	{
		return string.Join(Environment.NewLine,
			$"Run: {stats.Name}",
			$"  retained native refresh containers: {stats.Attempts}",
			$"  renderers alive after full GC: {stats.AliveRenderers}/{stats.Attempts}",
			$"  adapters alive after full GC: {stats.AliveAdapters}/{stats.Attempts}",
			$"  ListViews alive after full GC: {stats.AliveListViews}/{stats.Attempts}",
			$"  item sources alive after full GC: {stats.AliveItemSources}/{stats.Attempts}",
			$"  payloads alive after full GC: {stats.AlivePayloads}/{stats.Attempts}",
			$"  retained payload bytes: {FormatBytes(stats.RetainedPayloadBytes)} ({stats.RetainedPayloadBytes * 100.0 / (PayloadBytes * stats.Attempts):0.0}%)");
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

internal sealed class CleanupCapableListViewRenderer : ListViewRenderer
{
	public CleanupCapableListViewRenderer(Context context)
		: base(context)
	{
	}

	public void RunOldElementCleanup(ListView oldElement)
	{
		OnElementChanged(new ElementChangedEventArgs<ListView>(oldElement, null));
	}
}

internal static class ReproSession
{
	const int Attempts = 80;
	const int PayloadBytes = 1024 * 1024;

	static readonly FieldInfo AdapterField =
		typeof(ListViewRenderer).GetField("_adapter", BindingFlags.Instance | BindingFlags.NonPublic)
		?? throw new MissingFieldException(nameof(ListViewRenderer), "_adapter");

	public static async Task<ReproReport> RunAsync(IMauiContext mauiContext)
	{
		await Task.Yield();

		ForceFullGc();
		var baseline = GC.GetTotalMemory(forceFullCollection: true);

		var control = await RunScenarioAsync(
			mauiContext,
			"control: run ListViewRenderer old-element cleanup before disconnect",
			runOldElementCleanup: true);

		var current = await RunScenarioAsync(
			mauiContext,
			"current: disconnect leaves adapter/listener state on retained native refresh container",
			runOldElementCleanup: false);

		ForceFullGc();
		var final = GC.GetTotalMemory(forceFullCollection: true);

		return new ReproReport(Attempts, PayloadBytes, control, current, baseline, final);
	}

	static async Task<RunStats> RunScenarioAsync(IMauiContext mauiContext, string name, bool runOldElementCleanup)
	{
		var retainedNativeRefreshContainers = new List<SwipeRefreshLayout>(Attempts);
		var rendererRefs = new List<WeakReference<CleanupCapableListViewRenderer>>(Attempts);
		var adapterRefs = new List<WeakReference<object>>(Attempts);
		var listViewRefs = new List<WeakReference<ListView>>(Attempts);
		var itemSourceRefs = new List<WeakReference<PayloadItem[]>>(Attempts);
		var payloadRefs = new List<WeakReference<Payload>>(Attempts);

		for (var i = 0; i < Attempts; i++)
		{
			CreateDisconnectedListViewRenderer(
				mauiContext,
				runOldElementCleanup,
				retainedNativeRefreshContainers,
				rendererRefs,
				adapterRefs,
				listViewRefs,
				itemSourceRefs,
				payloadRefs,
				i);

			if (i % 10 == 0)
				await Task.Yield();
		}

		ForceFullGc();
		GC.KeepAlive(retainedNativeRefreshContainers);

		var aliveRenderers = rendererRefs.Count(static wr => wr.TryGetTarget(out _));
		var aliveAdapters = adapterRefs.Count(static wr => wr.TryGetTarget(out _));
		var aliveListViews = listViewRefs.Count(static wr => wr.TryGetTarget(out _));
		var aliveItemSources = itemSourceRefs.Count(static wr => wr.TryGetTarget(out _));
		var alivePayloads = payloadRefs.Count(static wr => wr.TryGetTarget(out _));

		return new RunStats(
			name,
			Attempts,
			aliveRenderers,
			aliveAdapters,
			aliveListViews,
			aliveItemSources,
			alivePayloads,
			(long)alivePayloads * PayloadBytes);
	}

	static void CreateDisconnectedListViewRenderer(
		IMauiContext mauiContext,
		bool runOldElementCleanup,
		List<SwipeRefreshLayout> retainedNativeRefreshContainers,
		List<WeakReference<CleanupCapableListViewRenderer>> rendererRefs,
		List<WeakReference<object>> adapterRefs,
		List<WeakReference<ListView>> listViewRefs,
		List<WeakReference<PayloadItem[]>> itemSourceRefs,
		List<WeakReference<Payload>> payloadRefs,
		int index)
	{
		var payload = new Payload(index, PayloadBytes);
		var itemSource = new[] { new PayloadItem($"Invoice batch {index}", payload) };
		var listView = new ListView(ListViewCachingStrategy.RecycleElement)
		{
			IsPullToRefreshEnabled = true,
			ItemsSource = itemSource,
			ItemTemplate = new DataTemplate(static () =>
			{
				var cell = new TextCell();
				cell.SetBinding(TextCell.TextProperty, nameof(PayloadItem.Title));
				return cell;
			})
		};

		payloadRefs.Add(new WeakReference<Payload>(payload));
		itemSourceRefs.Add(new WeakReference<PayloadItem[]>(itemSource));
		listViewRefs.Add(new WeakReference<ListView>(listView));

		var context = mauiContext.Context ?? throw new InvalidOperationException("Android context is not available.");
		var renderer = new CleanupCapableListViewRenderer(context);
		((IElementHandler)renderer).SetMauiContext(mauiContext);
		((IElementHandler)renderer).SetVirtualView(listView);
		rendererRefs.Add(new WeakReference<CleanupCapableListViewRenderer>(renderer));

		if (AdapterField.GetValue(renderer) is { } adapter)
			adapterRefs.Add(new WeakReference<object>(adapter));

		var nativeRefreshContainer = ((IPlatformViewHandler)renderer).ContainerView as SwipeRefreshLayout
			?? throw new InvalidOperationException("Expected SwipeRefreshLayout container.");
		retainedNativeRefreshContainers.Add(nativeRefreshContainer);

		if (runOldElementCleanup)
			renderer.RunOldElementCleanup(listView);

		((IElementHandler)renderer).DisconnectHandler();
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

	sealed class PayloadItem
	{
		public PayloadItem(string title, Payload payload)
		{
			Title = title;
			Payload = payload;
		}

		public string Title { get; }

		public Payload Payload { get; }
	}

	sealed class Payload
	{
		public Payload(int id, int byteCount)
		{
			Id = id;
			Bytes = new byte[byteCount];
			Bytes[0] = (byte)(id % 251);
			Bytes[^1] = (byte)((id + 1) % 251);
		}

		public int Id { get; }

		public byte[] Bytes { get; }
	}
}
