#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Android.Content;
using Android.Views;
using AndroidX.SwipeRefreshLayout.Widget;
using Microsoft.Maui;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Controls.Handlers.Compatibility;
using Microsoft.Maui.Handlers;
using AListView = Android.Widget.ListView;
using AView = Android.Views.View;

namespace AndroidListViewHeaderFooterHandlerRetentionRepro;

public sealed record RunStats(
	string Name,
	int Attempts,
	int AliveRenderers,
	int AliveAdapters,
	int AliveListViews,
	int AliveHeaderContainers,
	int AliveFooterContainers,
	int AliveHeaderHandlers,
	int AliveFooterHandlers,
	int AliveHeaders,
	int AliveFooters,
	int AliveHeaderPayloads,
	int AliveFooterPayloads,
	long RetainedPayloadBytes);

public sealed record ReproReport(
	int Attempts,
	int PayloadBytesPerHeaderOrFooter,
	RunStats Control,
	RunStats Current,
	long ManagedHeapBaseline,
	long ManagedHeapFinal)
{
	public int ExpectedPayloads => Attempts * 2;

	public bool LeakProved =>
		Control.AliveRenderers == 0 &&
		Current.AliveRenderers == 0 &&
		Control.AliveAdapters == 0 &&
		Current.AliveAdapters == 0 &&
		Control.AliveHeaderHandlers == 0 &&
		Control.AliveFooterHandlers == 0 &&
		Control.AliveHeaderPayloads == 0 &&
		Control.AliveFooterPayloads == 0 &&
		Current.AliveHeaderContainers == Attempts &&
		Current.AliveFooterContainers == Attempts &&
		Current.AliveHeaderHandlers == Attempts &&
		Current.AliveFooterHandlers == Attempts &&
		Current.AliveHeaderPayloads == Attempts &&
		Current.AliveFooterPayloads == Attempts;

	public string ToText()
	{
		return string.Join(Environment.NewLine,
			"AndroidListViewHeaderFooterHandlerRetentionRepro",
			$"Attempts: {Attempts}",
			$"Payload bytes per header/footer: {PayloadBytesPerHeaderOrFooter:N0}",
			$"Expected retained payload count in current run: {ExpectedPayloads:N0}",
			"Known ListView roots neutralized in both runs: native Adapter, scroll listener, SwipeRefreshLayout refresh listener",
			$"Leak proved: {LeakProved}",
			string.Empty,
			Format(Control),
			string.Empty,
			Format(Current),
			string.Empty,
			$"Control retained payload: {FormatBytes(Control.RetainedPayloadBytes)}",
			$"Current retained payload: {FormatBytes(Current.RetainedPayloadBytes)}",
			$"Managed heap baseline: {FormatBytes(ManagedHeapBaseline)}",
			$"Managed heap final: {FormatBytes(ManagedHeapFinal)}",
			$"Managed heap delta: {FormatBytes(ManagedHeapFinal - ManagedHeapBaseline)}",
			$"RESULT: {(LeakProved ? "PROVEN" : "NOT PROVEN")}");
	}

	string Format(RunStats stats)
	{
		return string.Join(Environment.NewLine,
			$"Run: {stats.Name}",
			$"  retained native header containers: {stats.AliveHeaderContainers}/{stats.Attempts}",
			$"  retained native footer containers: {stats.AliveFooterContainers}/{stats.Attempts}",
			$"  renderers alive after full GC: {stats.AliveRenderers}/{stats.Attempts}",
			$"  adapters alive after full GC: {stats.AliveAdapters}/{stats.Attempts}",
			$"  ListViews alive after full GC: {stats.AliveListViews}/{stats.Attempts}",
			$"  header handlers alive after full GC: {stats.AliveHeaderHandlers}/{stats.Attempts}",
			$"  footer handlers alive after full GC: {stats.AliveFooterHandlers}/{stats.Attempts}",
			$"  header views alive after full GC: {stats.AliveHeaders}/{stats.Attempts}",
			$"  footer views alive after full GC: {stats.AliveFooters}/{stats.Attempts}",
			$"  header payloads alive after full GC: {stats.AliveHeaderPayloads}/{stats.Attempts}",
			$"  footer payloads alive after full GC: {stats.AliveFooterPayloads}/{stats.Attempts}",
			$"  retained payload bytes: {FormatBytes(stats.RetainedPayloadBytes)}");
	}

	static string FormatBytes(long bytes)
	{
		var sign = bytes < 0 ? "-" : "";
		var value = Math.Abs((double)bytes);
		if (value >= 1024 * 1024)
			return $"{sign}{value / 1024 / 1024:N1} MiB";
		if (value >= 1024)
			return $"{sign}{value / 1024:N1} KiB";
		return $"{bytes:N0} B";
	}
}

internal sealed class CleanupCapableListViewRenderer : ListViewRenderer
{
	static readonly BindingFlags InstanceNonPublic = BindingFlags.Instance | BindingFlags.NonPublic;

	static readonly FieldInfo AdapterField =
		typeof(ListViewRenderer).GetField("_adapter", InstanceNonPublic)
		?? throw new MissingFieldException(nameof(ListViewRenderer), "_adapter");

	static readonly FieldInfo HeaderViewField =
		typeof(ListViewRenderer).GetField("_headerView", InstanceNonPublic)
		?? throw new MissingFieldException(nameof(ListViewRenderer), "_headerView");

	static readonly FieldInfo FooterViewField =
		typeof(ListViewRenderer).GetField("_footerView", InstanceNonPublic)
		?? throw new MissingFieldException(nameof(ListViewRenderer), "_footerView");

	static readonly FieldInfo RefreshField =
		typeof(ListViewRenderer).GetField("_refresh", InstanceNonPublic)
		?? throw new MissingFieldException(nameof(ListViewRenderer), "_refresh");

	public CleanupCapableListViewRenderer(Context context)
		: base(context)
	{
	}

	public object? CurrentAdapter => AdapterField.GetValue(this);

	public AView HeaderContainer => (AView)(HeaderViewField.GetValue(this)
		?? throw new InvalidOperationException("ListView header container was not created."));

	public AView FooterContainer => (AView)(FooterViewField.GetValue(this)
		?? throw new InvalidOperationException("ListView footer container was not created."));

	public IPlatformViewHandler? HeaderHandler => GetContainerChild(HeaderContainer);

	public IPlatformViewHandler? FooterHandler => GetContainerChild(FooterContainer);

	public void NeutralizeKnownListViewRoots()
	{
		if (Control is not null)
		{
			Control.Adapter = null;
			Control.SetOnScrollListener(null);
		}

		if (RefreshField.GetValue(this) is SwipeRefreshLayout refresh)
			refresh.SetOnRefreshListener(null);

		if (AdapterField.GetValue(this) is IDisposable adapter)
			adapter.Dispose();

		AdapterField.SetValue(this, null);
	}

	public void ClearHeaderFooterContainers()
	{
		ClearContainerChild(HeaderContainer);
		ClearContainerChild(FooterContainer);
	}

	static IPlatformViewHandler? GetContainerChild(AView container)
	{
		var childField = container.GetType().GetField("_child", InstanceNonPublic)
			?? throw new MissingFieldException(container.GetType().FullName, "_child");

		return childField.GetValue(container) as IPlatformViewHandler;
	}

	static void ClearContainerChild(AView container)
	{
		var child = GetContainerChild(container);
		var childProperty = container.GetType().GetProperty("Child", BindingFlags.Instance | BindingFlags.Public)
			?? throw new MissingMemberException(container.GetType().FullName, "Child");

		childProperty.SetValue(container, null);

		if (child is IElementHandler handler)
			handler.DisconnectHandler();
	}
}

internal sealed class ContextHostElement : Element
{
}

internal sealed class ContextHostHandler : IElementHandler
{
	IElement? _virtualView;

	public ContextHostHandler(IMauiContext mauiContext)
	{
		MauiContext = mauiContext;
	}

	public object? PlatformView => null;

	public IElement? VirtualView => _virtualView;

	public IMauiContext? MauiContext { get; private set; }

	public void SetMauiContext(IMauiContext mauiContext)
	{
		MauiContext = mauiContext;
	}

	public void SetVirtualView(IElement view)
	{
		_virtualView = view;
	}

	public void UpdateValue(string property)
	{
	}

	public void Invoke(string command, object? args = null)
	{
	}

	public void DisconnectHandler()
	{
		_virtualView = null;
		MauiContext = null;
	}
}

internal static class ReproSession
{
	const int Attempts = 80;
	const int PayloadBytes = 512 * 1024;

	public static async Task<ReproReport> RunAsync(IMauiContext mauiContext)
	{
		await Task.Yield();

		ForceFullGc();
		var baseline = GC.GetTotalMemory(forceFullCollection: true);

		var control = await RunScenarioAsync(
			mauiContext,
			"control: clear native header/footer container children before disconnect",
			clearHeaderFooterContainers: true);

		var current = await RunScenarioAsync(
			mauiContext,
			"current: disconnect leaves native header/footer containers pointing at handlers",
			clearHeaderFooterContainers: false);

		ForceFullGc();
		var final = GC.GetTotalMemory(forceFullCollection: true);

		return new ReproReport(Attempts, PayloadBytes, control, current, baseline, final);
	}

	static async Task<RunStats> RunScenarioAsync(
		IMauiContext mauiContext,
		string name,
		bool clearHeaderFooterContainers)
	{
		var retainedHeaderContainers = new List<AView>(Attempts);
		var retainedFooterContainers = new List<AView>(Attempts);
		var rendererRefs = new List<WeakReference<CleanupCapableListViewRenderer>>(Attempts);
		var adapterRefs = new List<WeakReference<object>>(Attempts);
		var listViewRefs = new List<WeakReference<ListView>>(Attempts);
		var headerHandlerRefs = new List<WeakReference<IPlatformViewHandler>>(Attempts);
		var footerHandlerRefs = new List<WeakReference<IPlatformViewHandler>>(Attempts);
		var headerRefs = new List<WeakReference<Label>>(Attempts);
		var footerRefs = new List<WeakReference<Label>>(Attempts);
		var headerPayloadRefs = new List<WeakReference<Payload>>(Attempts);
		var footerPayloadRefs = new List<WeakReference<Payload>>(Attempts);

		for (var i = 0; i < Attempts; i++)
		{
			CreateDisconnectedListViewRenderer(
				mauiContext,
				clearHeaderFooterContainers,
				retainedHeaderContainers,
				retainedFooterContainers,
				rendererRefs,
				adapterRefs,
				listViewRefs,
				headerHandlerRefs,
				footerHandlerRefs,
				headerRefs,
				footerRefs,
				headerPayloadRefs,
				footerPayloadRefs,
				i);

			if (i % 10 == 0)
				await Task.Yield();
		}

		ForceFullGc();
		GC.KeepAlive(retainedHeaderContainers);
		GC.KeepAlive(retainedFooterContainers);

		var aliveHeaderPayloads = headerPayloadRefs.Count(static wr => wr.TryGetTarget(out _));
		var aliveFooterPayloads = footerPayloadRefs.Count(static wr => wr.TryGetTarget(out _));

		return new RunStats(
			name,
			Attempts,
			rendererRefs.Count(static wr => wr.TryGetTarget(out _)),
			adapterRefs.Count(static wr => wr.TryGetTarget(out _)),
			listViewRefs.Count(static wr => wr.TryGetTarget(out _)),
			retainedHeaderContainers.Count,
			retainedFooterContainers.Count,
			headerHandlerRefs.Count(static wr => wr.TryGetTarget(out _)),
			footerHandlerRefs.Count(static wr => wr.TryGetTarget(out _)),
			headerRefs.Count(static wr => wr.TryGetTarget(out _)),
			footerRefs.Count(static wr => wr.TryGetTarget(out _)),
			aliveHeaderPayloads,
			aliveFooterPayloads,
			(long)(aliveHeaderPayloads + aliveFooterPayloads) * PayloadBytes);
	}

	static void CreateDisconnectedListViewRenderer(
		IMauiContext mauiContext,
		bool clearHeaderFooterContainers,
		List<AView> retainedHeaderContainers,
		List<AView> retainedFooterContainers,
		List<WeakReference<CleanupCapableListViewRenderer>> rendererRefs,
		List<WeakReference<object>> adapterRefs,
		List<WeakReference<ListView>> listViewRefs,
		List<WeakReference<IPlatformViewHandler>> headerHandlerRefs,
		List<WeakReference<IPlatformViewHandler>> footerHandlerRefs,
		List<WeakReference<Label>> headerRefs,
		List<WeakReference<Label>> footerRefs,
		List<WeakReference<Payload>> headerPayloadRefs,
		List<WeakReference<Payload>> footerPayloadRefs,
		int index)
	{
		var headerPayload = new Payload($"header-{index:D4}", PayloadBytes);
		var footerPayload = new Payload($"footer-{index:D4}", PayloadBytes);
		var header = new Label
		{
			Text = $"Account header {index:D4}",
			BindingContext = headerPayload
		};
		var footer = new Label
		{
			Text = $"Account footer {index:D4}",
			BindingContext = footerPayload
		};

		var listView = new ListView(ListViewCachingStrategy.RecycleElement)
		{
			Header = header,
			Footer = footer,
			ItemsSource = new[] { $"row-{index:D4}" },
			ItemTemplate = new DataTemplate(static () => new TextCell { Text = "row" })
		};
		var contextHost = new ContextHostElement
		{
			Handler = new ContextHostHandler(mauiContext)
		};
		listView.Parent = contextHost;

		headerPayloadRefs.Add(new WeakReference<Payload>(headerPayload));
		footerPayloadRefs.Add(new WeakReference<Payload>(footerPayload));
		headerRefs.Add(new WeakReference<Label>(header));
		footerRefs.Add(new WeakReference<Label>(footer));
		listViewRefs.Add(new WeakReference<ListView>(listView));

		var context = mauiContext.Context ?? throw new InvalidOperationException("Android context is not available.");
		var renderer = new CleanupCapableListViewRenderer(context);
		((IElementHandler)renderer).SetMauiContext(mauiContext);
		((IElementHandler)renderer).SetVirtualView(listView);
		rendererRefs.Add(new WeakReference<CleanupCapableListViewRenderer>(renderer));
		listView.Parent = null;
		contextHost.Handler = null;

		if (renderer.CurrentAdapter is { } adapter)
			adapterRefs.Add(new WeakReference<object>(adapter));

		if (renderer.HeaderHandler is not { } headerHandler)
			throw new InvalidOperationException("Expected retained header handler in native header container.");
		if (renderer.FooterHandler is not { } footerHandler)
			throw new InvalidOperationException("Expected retained footer handler in native footer container.");

		headerHandlerRefs.Add(new WeakReference<IPlatformViewHandler>(headerHandler));
		footerHandlerRefs.Add(new WeakReference<IPlatformViewHandler>(footerHandler));

		retainedHeaderContainers.Add(renderer.HeaderContainer);
		retainedFooterContainers.Add(renderer.FooterContainer);

		renderer.NeutralizeKnownListViewRoots();

		if (clearHeaderFooterContainers)
			renderer.ClearHeaderFooterContainers();

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

	sealed class Payload
	{
		public Payload(string id, int byteCount)
		{
			Id = id;
			Bytes = new byte[byteCount];
			Bytes[0] = (byte)(id.Length % 251);
			Bytes[^1] = (byte)((id.Length + 1) % 251);
		}

		public string Id { get; }

		public byte[] Bytes { get; }
	}
}
