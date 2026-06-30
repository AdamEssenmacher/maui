#nullable enable

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Android.Content;
using Android.Widget;
using AndroidX.RecyclerView.Widget;
using Microsoft.Maui;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Controls.Handlers.Items;
using Microsoft.Maui.Graphics;

namespace AndroidCollectionViewHeaderFooterHolderRetentionRepro;

internal static class ReproSession
{
	internal const int Cycles = 80;
	const int ContextPayloadBytes = 512 * 1024;
	const int HeaderPayloadBytes = 512 * 1024;
	const int FooterPayloadBytes = 512 * 1024;
	const int HoldersPerCycle = 2;
	const int TotalPayloadBytesPerCycle = ContextPayloadBytes + HeaderPayloadBytes + FooterPayloadBytes;

	static readonly BindingFlags InstanceNonPublic = BindingFlags.Instance | BindingFlags.NonPublic;
	static readonly Type SimpleViewHolderType = typeof(ItemsViewAdapter<CollectionView, IItemsViewSource>).Assembly.GetType("Microsoft.Maui.Controls.Handlers.Items.SimpleViewHolder")
		?? throw new MissingMemberException("Microsoft.Maui.Controls.Handlers.Items.SimpleViewHolder");
	static readonly FieldInfo SimpleItemViewField = SimpleViewHolderType.GetField("_itemView", InstanceNonPublic)
		?? throw new MissingFieldException(SimpleViewHolderType.FullName, "_itemView");
	static readonly FieldInfo SimpleViewField = SimpleViewHolderType.GetField("<View>k__BackingField", InstanceNonPublic)
		?? throw new MissingFieldException(SimpleViewHolderType.FullName, "<View>k__BackingField");
	static readonly MethodInfo ItemContentRecycleMethod = typeof(ItemContentView).GetMethod("Recycle", InstanceNonPublic)
		?? throw new MissingMethodException(typeof(ItemContentView).FullName, "Recycle");
	static readonly FieldInfo ItemContentField = typeof(ItemContentView).GetField("Content", InstanceNonPublic)
		?? throw new MissingFieldException(typeof(ItemContentView).FullName, "Content");

	static readonly List<RecyclerView.ViewHolder> RetainedHolderRoots = new();

	public static async Task<ReproReport> RunAsync(IMauiContext rootContext)
	{
		RetainedHolderRoots.Clear();
		ForceFullGc();
		var baselineBytes = GC.GetTotalMemory(true);

		var androidContext = rootContext.Context
			?? Android.App.Application.Context
			?? throw new InvalidOperationException("No Android context is available.");

		var control = await RunScenarioAsync(
			rootContext.Services,
			androidContext,
			"control: explicitly recycle SimpleViewHolder ItemContentView and clear holder references",
			explicitHolderCleanup: true);

		var current = await RunScenarioAsync(
			rootContext.Services,
			androidContext,
			"current: StructuredItemsViewAdapter recycle ignores SimpleViewHolder",
			explicitHolderCleanup: false);

		ForceFullGc();
		var finalBytes = GC.GetTotalMemory(true);

		GC.KeepAlive(RetainedHolderRoots);

		return new ReproReport(
			Cycles,
			HoldersPerCycle,
			ContextPayloadBytes,
			HeaderPayloadBytes,
			FooterPayloadBytes,
			TotalPayloadBytesPerCycle,
			baselineBytes,
			finalBytes,
			control,
			current);
	}

	static async Task<ScenarioResult> RunScenarioAsync(
		IServiceProvider rootServices,
		Context androidContext,
		string name,
		bool explicitHolderCleanup)
	{
		var tracked = new List<TrackedCycle>(Cycles);

		for (var i = 0; i < Cycles; i++)
		{
			CreateCycle(rootServices, androidContext, i, tracked, explicitHolderCleanup);

			if (i % 16 == 0)
				await Task.Yield();
		}

		ForceFullGc();

		return ScenarioResult.From(name, tracked);
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	static void CreateCycle(
		IServiceProvider rootServices,
		Context androidContext,
		int cycle,
		List<TrackedCycle> tracked,
		bool explicitHolderCleanup)
	{
		var serviceProvider = new PayloadServiceProvider(rootServices, cycle, ContextPayloadBytes);
		var cycleContext = new MauiContext(serviceProvider, androidContext);
		var headerPayload = new Payload("header", cycle, HeaderPayloadBytes);
		var footerPayload = new Payload("footer", cycle, FooterPayloadBytes);
		var header = CreateHeaderFooterView("Header", cycle, headerPayload);
		var footer = CreateHeaderFooterView("Footer", cycle, footerPayload);
		var collectionView = new CollectionView
		{
			Header = header,
			Footer = footer,
			ItemsSource = Array.Empty<object>()
		};

		var contextHandler = new ContextOnlyHandler(androidContext);
		contextHandler.SetMauiContext(cycleContext);
		contextHandler.SetVirtualView(collectionView);

		var adapter = new ProbeStructuredItemsViewAdapter(collectionView);
		using var parent = new FrameLayout(androidContext);

		var headerHolder = adapter.OnCreateViewHolder(parent, adapter.GetItemViewType(0));
		var footerHolder = adapter.OnCreateViewHolder(parent, adapter.GetItemViewType(adapter.ItemCount - 1));

		ValidateHolder(headerHolder, header);
		ValidateHolder(footerHolder, footer);

		var headerHandler = header.Handler as IElementHandler
			?? throw new InvalidOperationException("Header view did not receive a handler.");
		var footerHandler = footer.Handler as IElementHandler
			?? throw new InvalidOperationException("Footer view did not receive a handler.");
		var headerItemContentView = (ItemContentView)headerHolder.ItemView;
		var footerItemContentView = (ItemContentView)footerHolder.ItemView;

		collectionView.Header = null;
		collectionView.Footer = null;
		adapter.OnViewRecycled(headerHolder);
		adapter.OnViewRecycled(footerHolder);
		adapter.Dispose();
		contextHandler.DisconnectHandler();

		if (explicitHolderCleanup)
		{
			ExplicitlyCleanupSimpleHolder(headerHolder, collectionView);
			ExplicitlyCleanupSimpleHolder(footerHolder, collectionView);
		}

		RetainedHolderRoots.Add(headerHolder);
		RetainedHolderRoots.Add(footerHolder);
		tracked.Add(TrackedCycle.Create(
			cycle,
			collectionView,
			header,
			footer,
			headerHandler,
			footerHandler,
			headerItemContentView,
			footerItemContentView,
			cycleContext,
			serviceProvider,
			headerPayload,
			footerPayload));

		serviceProvider = null!;
		cycleContext = null!;
		headerPayload = null!;
		footerPayload = null!;
		header = null!;
		footer = null!;
		collectionView = null!;
		headerHandler = null!;
		footerHandler = null!;
		headerItemContentView = null!;
		footerItemContentView = null!;
	}

	static View CreateHeaderFooterView(string kind, int cycle, Payload payload)
	{
		return new Grid
		{
			WidthRequest = 120,
			HeightRequest = 40,
			BackgroundColor = kind == "Header" ? Colors.LightBlue : Colors.LightGreen,
			BindingContext = payload,
			Children =
			{
				new BoxView
				{
					WidthRequest = 16,
					HeightRequest = 16,
					Color = kind == "Header" ? Colors.Navy : Colors.DarkGreen,
					AutomationId = $"{kind}-{cycle:D4}"
				}
			}
		};
	}

	static void ValidateHolder(RecyclerView.ViewHolder holder, View expectedView)
	{
		if (!SimpleViewHolderType.IsInstanceOfType(holder))
			throw new InvalidOperationException($"Expected SimpleViewHolder, got {holder.GetType().FullName}.");

		if (holder.ItemView is not ItemContentView itemContentView)
			throw new InvalidOperationException($"Expected ItemContentView, got {holder.ItemView.GetType().FullName}.");

		if (GetHolderView(holder) != expectedView)
			throw new InvalidOperationException("SimpleViewHolder does not reference the expected Forms view.");

		if (GetItemContentHandler(itemContentView) is null)
			throw new InvalidOperationException("ItemContentView did not retain the created child handler.");
	}

	static View? GetHolderView(RecyclerView.ViewHolder holder)
	{
		return SimpleViewField.GetValue(holder) as View;
	}

	static object? GetItemContentHandler(ItemContentView itemContentView)
	{
		return ItemContentField.GetValue(itemContentView);
	}

	static bool ItemContentHasHandler(ItemContentView itemContentView)
	{
		return GetItemContentHandler(itemContentView) is not null;
	}

	static bool HolderHasFormsView(RecyclerView.ViewHolder holder)
	{
		return GetHolderView(holder) is not null;
	}

	static void ExplicitlyCleanupSimpleHolder(RecyclerView.ViewHolder holder, CollectionView collectionView)
	{
		if (GetHolderView(holder) is View formsView)
		{
			if (formsView.Parent == collectionView)
				collectionView.RemoveLogicalChild(formsView);

			formsView.BindingContext = null;
		}

		if (holder.ItemView is ItemContentView itemContentView)
			ItemContentRecycleMethod.Invoke(itemContentView, null);

		SimpleItemViewField.SetValue(holder, null);
		SimpleViewField.SetValue(holder, null);
	}

	static void ForceFullGc()
	{
		for (var i = 0; i < 4; i++)
		{
			GC.Collect();
			GC.WaitForPendingFinalizers();
			GC.Collect();
			Java.Lang.JavaSystem.Gc();
			Thread.Sleep(100);
		}
	}

	sealed class ProbeStructuredItemsViewAdapter : StructuredItemsViewAdapter<CollectionView, IItemsViewSource>
	{
		public ProbeStructuredItemsViewAdapter(CollectionView itemsView)
			: base(itemsView)
		{
		}
	}

	sealed class ContextOnlyHandler : IViewHandler
	{
		readonly Android.Views.View _platformView;

		public ContextOnlyHandler(Context context)
		{
			_platformView = new Android.Views.View(context);
		}

		public bool HasContainer { get; set; }

		public object? ContainerView => null;

		public object? PlatformView => _platformView;

		public IElement? VirtualView { get; private set; }

		IView? IViewHandler.VirtualView => VirtualView as IView;

		public IMauiContext? MauiContext { get; private set; }

		public void SetMauiContext(IMauiContext mauiContext)
		{
			MauiContext = mauiContext;
		}

		public void SetVirtualView(IElement view)
		{
			VirtualView = view;
			view.Handler = this;
		}

		public void UpdateValue(string property)
		{
		}

		public void Invoke(string command, object? args = null)
		{
		}

		public void DisconnectHandler()
		{
			if (VirtualView?.Handler == this)
				VirtualView.Handler = null;

			VirtualView = null;
			MauiContext = null;
		}

		public Size GetDesiredSize(double widthConstraint, double heightConstraint)
		{
			return Size.Zero;
		}

		public void PlatformArrange(Rect frame)
		{
		}
	}

	internal sealed record TrackedCycle(
		int Cycle,
		WeakReference<CollectionView> CollectionView,
		WeakReference<View> HeaderView,
		WeakReference<View> FooterView,
		WeakReference<IElementHandler> HeaderHandler,
		WeakReference<IElementHandler> FooterHandler,
		WeakReference<ItemContentView> HeaderItemContentView,
		WeakReference<ItemContentView> FooterItemContentView,
		WeakReference<MauiContext> MauiContext,
		WeakReference<PayloadServiceProvider> ServiceProvider,
		WeakReference<byte[]> ContextPayload,
		WeakReference<Payload> HeaderPayload,
		WeakReference<byte[]> HeaderPayloadBytes,
		WeakReference<Payload> FooterPayload,
		WeakReference<byte[]> FooterPayloadBytes)
	{
		public static TrackedCycle Create(
			int cycle,
			CollectionView collectionView,
			View headerView,
			View footerView,
			IElementHandler headerHandler,
			IElementHandler footerHandler,
			ItemContentView headerItemContentView,
			ItemContentView footerItemContentView,
			MauiContext mauiContext,
			PayloadServiceProvider serviceProvider,
			Payload headerPayload,
			Payload footerPayload)
		{
			return new TrackedCycle(
				cycle,
				new WeakReference<CollectionView>(collectionView),
				new WeakReference<View>(headerView),
				new WeakReference<View>(footerView),
				new WeakReference<IElementHandler>(headerHandler),
				new WeakReference<IElementHandler>(footerHandler),
				new WeakReference<ItemContentView>(headerItemContentView),
				new WeakReference<ItemContentView>(footerItemContentView),
				new WeakReference<MauiContext>(mauiContext),
				new WeakReference<PayloadServiceProvider>(serviceProvider),
				new WeakReference<byte[]>(serviceProvider.Payload),
				new WeakReference<Payload>(headerPayload),
				new WeakReference<byte[]>(headerPayload.Bytes),
				new WeakReference<Payload>(footerPayload),
				new WeakReference<byte[]>(footerPayload.Bytes));
		}
	}

	internal sealed record ScenarioResult(
		string Name,
		int TrackedCycles,
		int AliveHolderRoots,
		int AliveCollectionViews,
		int AliveHeaderViews,
		int AliveFooterViews,
		int AliveHeaderHandlers,
		int AliveFooterHandlers,
		int AliveMauiContexts,
		int AliveServiceProviders,
		int AliveContextPayloads,
		int AliveHeaderPayloads,
		int AliveHeaderPayloadByteArrays,
		int AliveFooterPayloads,
		int AliveFooterPayloadByteArrays,
		int ItemContentViewsWithHandler,
		int HoldersWithFormsView,
		long RetainedContextPayloadBytes,
		long RetainedHeaderPayloadBytes,
		long RetainedFooterPayloadBytes)
	{
		internal static ScenarioResult From(string name, IReadOnlyList<TrackedCycle> tracked)
		{
			var aliveHolderRoots = RetainedHolderRoots.Count;
			var aliveCollectionViews = 0;
			var aliveHeaderViews = 0;
			var aliveFooterViews = 0;
			var aliveHeaderHandlers = 0;
			var aliveFooterHandlers = 0;
			var aliveMauiContexts = 0;
			var aliveServiceProviders = 0;
			var aliveContextPayloads = 0;
			var aliveHeaderPayloads = 0;
			var aliveHeaderPayloadByteArrays = 0;
			var aliveFooterPayloads = 0;
			var aliveFooterPayloadByteArrays = 0;
			var itemContentViewsWithHandler = 0;
			var holdersWithFormsView = 0;
			long retainedContextPayloadBytes = 0;
			long retainedHeaderPayloadBytes = 0;
			long retainedFooterPayloadBytes = 0;

			foreach (var holder in RetainedHolderRoots)
			{
				if (HolderHasFormsView(holder))
					holdersWithFormsView++;
			}

			foreach (var cycle in tracked)
			{
				if (cycle.CollectionView.TryGetTarget(out _))
					aliveCollectionViews++;

				if (cycle.HeaderView.TryGetTarget(out _))
					aliveHeaderViews++;

				if (cycle.FooterView.TryGetTarget(out _))
					aliveFooterViews++;

				if (cycle.HeaderHandler.TryGetTarget(out _))
					aliveHeaderHandlers++;

				if (cycle.FooterHandler.TryGetTarget(out _))
					aliveFooterHandlers++;

				if (cycle.HeaderItemContentView.TryGetTarget(out var headerItemContentView) &&
					ItemContentHasHandler(headerItemContentView))
				{
					itemContentViewsWithHandler++;
				}

				if (cycle.FooterItemContentView.TryGetTarget(out var footerItemContentView) &&
					ItemContentHasHandler(footerItemContentView))
				{
					itemContentViewsWithHandler++;
				}

				if (cycle.MauiContext.TryGetTarget(out _))
					aliveMauiContexts++;

				if (cycle.ServiceProvider.TryGetTarget(out _))
					aliveServiceProviders++;

				if (cycle.ContextPayload.TryGetTarget(out _))
				{
					aliveContextPayloads++;
					retainedContextPayloadBytes += ContextPayloadBytes;
				}

				if (cycle.HeaderPayload.TryGetTarget(out _))
					aliveHeaderPayloads++;

				if (cycle.HeaderPayloadBytes.TryGetTarget(out _))
				{
					aliveHeaderPayloadByteArrays++;
					retainedHeaderPayloadBytes += HeaderPayloadBytes;
				}

				if (cycle.FooterPayload.TryGetTarget(out _))
					aliveFooterPayloads++;

				if (cycle.FooterPayloadBytes.TryGetTarget(out _))
				{
					aliveFooterPayloadByteArrays++;
					retainedFooterPayloadBytes += FooterPayloadBytes;
				}
			}

			return new ScenarioResult(
				name,
				tracked.Count,
				aliveHolderRoots,
				aliveCollectionViews,
				aliveHeaderViews,
				aliveFooterViews,
				aliveHeaderHandlers,
				aliveFooterHandlers,
				aliveMauiContexts,
				aliveServiceProviders,
				aliveContextPayloads,
				aliveHeaderPayloads,
				aliveHeaderPayloadByteArrays,
				aliveFooterPayloads,
				aliveFooterPayloadByteArrays,
				itemContentViewsWithHandler,
				holdersWithFormsView,
				retainedContextPayloadBytes,
				retainedHeaderPayloadBytes,
				retainedFooterPayloadBytes);
		}
	}
}

internal sealed class PayloadServiceProvider : IServiceProvider
{
	readonly IServiceProvider _inner;

	public PayloadServiceProvider(IServiceProvider inner, int cycle, int payloadBytes)
	{
		_inner = inner;
		Cycle = cycle;
		Payload = new byte[payloadBytes];
		Array.Fill(Payload, (byte)(cycle % 251));
	}

	public int Cycle { get; }

	public byte[] Payload { get; }

	public object? GetService(Type serviceType)
	{
		return _inner.GetService(serviceType);
	}
}

internal sealed class Payload
{
	public Payload(string kind, int cycle, int payloadBytes)
	{
		Kind = kind;
		Cycle = cycle;
		Bytes = new byte[payloadBytes];
		Array.Fill(Bytes, (byte)((cycle + kind.Length) % 251));
	}

	public string Kind { get; }

	public int Cycle { get; }

	public byte[] Bytes { get; }
}

internal sealed record ReproReport(
	int Cycles,
	int HoldersPerCycle,
	int ContextPayloadBytes,
	int HeaderPayloadBytes,
	int FooterPayloadBytes,
	int TotalPayloadBytesPerCycle,
	long BaselineManagedBytes,
	long FinalManagedBytes,
	ReproSession.ScenarioResult Control,
	ReproSession.ScenarioResult Current)
{
	int HolderCount => Cycles * HoldersPerCycle;

	public bool LeakProved =>
		Control.ItemContentViewsWithHandler == 0 &&
		Control.HoldersWithFormsView == 0 &&
		Control.AliveHeaderHandlers == 0 &&
		Control.AliveFooterHandlers == 0 &&
		Control.AliveMauiContexts == 0 &&
		Control.AliveContextPayloads == 0 &&
		Control.AliveHeaderPayloadByteArrays == 0 &&
		Control.AliveFooterPayloadByteArrays == 0 &&
		Current.ItemContentViewsWithHandler == HolderCount &&
		Current.HoldersWithFormsView == HolderCount &&
		Current.AliveHeaderHandlers == Cycles &&
		Current.AliveFooterHandlers == Cycles &&
		Current.AliveMauiContexts == Cycles &&
		Current.AliveServiceProviders == Cycles &&
		Current.AliveContextPayloads == Cycles &&
		Current.AliveHeaderPayloadByteArrays == Cycles &&
		Current.AliveFooterPayloadByteArrays == Cycles;

	public string ToText()
	{
		var heapDeltaMiB = (FinalManagedBytes - BaselineManagedBytes) / 1024d / 1024d;
		var controlTotal = Control.RetainedContextPayloadBytes + Control.RetainedHeaderPayloadBytes + Control.RetainedFooterPayloadBytes;
		var currentTotal = Current.RetainedContextPayloadBytes + Current.RetainedHeaderPayloadBytes + Current.RetainedFooterPayloadBytes;

		return string.Join(Environment.NewLine,
			"AndroidCollectionViewHeaderFooterHolderRetentionRepro",
			$"Cycles: {Cycles}",
			$"Retained holder roots per cycle: {HoldersPerCycle}",
			$"Context payload bytes per cycle: {ContextPayloadBytes:N0}",
			$"Header binding payload bytes per cycle: {HeaderPayloadBytes:N0}",
			$"Footer binding payload bytes per cycle: {FooterPayloadBytes:N0}",
			$"Total payload bytes per cycle: {TotalPayloadBytesPerCycle:N0}",
			$"Baseline managed heap: {BaselineManagedBytes:N0} bytes",
			$"Final managed heap: {FinalManagedBytes:N0} bytes",
			$"Managed heap delta: {heapDeltaMiB:N1} MiB",
			$"Leak proved: {LeakProved}",
			string.Empty,
			Format(Control),
			string.Empty,
			Format(Current),
			string.Empty,
			$"Control retained payload: {FormatBytes(controlTotal)}",
			$"Current retained payload: {FormatBytes(currentTotal)}",
			$"Current retained context payload: {FormatBytes(Current.RetainedContextPayloadBytes)}",
			$"Current retained header binding payload: {FormatBytes(Current.RetainedHeaderPayloadBytes)}",
			$"Current retained footer binding payload: {FormatBytes(Current.RetainedFooterPayloadBytes)}",
			$"RESULT: {(LeakProved ? "PROVEN" : "NOT PROVEN")}");
	}

	static string Format(ReproSession.ScenarioResult result)
	{
		var holderCount = result.TrackedCycles * 2;

		return string.Join(Environment.NewLine,
			$"Run: {result.Name}",
			$"  tracked cycles: {result.TrackedCycles}",
			$"  expected holder roots for this run: {holderCount}",
			$"  cumulative retained holder roots: {result.AliveHolderRoots}",
			$"  alive CollectionViews: {result.AliveCollectionViews}/{result.TrackedCycles}",
			$"  alive header views: {result.AliveHeaderViews}/{result.TrackedCycles}",
			$"  alive footer views: {result.AliveFooterViews}/{result.TrackedCycles}",
			$"  alive header handlers: {result.AliveHeaderHandlers}/{result.TrackedCycles}",
			$"  alive footer handlers: {result.AliveFooterHandlers}/{result.TrackedCycles}",
			$"  alive MauiContexts: {result.AliveMauiContexts}/{result.TrackedCycles}",
			$"  alive service providers: {result.AliveServiceProviders}/{result.TrackedCycles}",
			$"  alive context payload byte arrays: {result.AliveContextPayloads}/{result.TrackedCycles}",
			$"  alive header payloads: {result.AliveHeaderPayloads}/{result.TrackedCycles}",
			$"  alive header payload byte arrays: {result.AliveHeaderPayloadByteArrays}/{result.TrackedCycles}",
			$"  alive footer payloads: {result.AliveFooterPayloads}/{result.TrackedCycles}",
			$"  alive footer payload byte arrays: {result.AliveFooterPayloadByteArrays}/{result.TrackedCycles}",
			$"  retained ItemContentViews with child handler: {result.ItemContentViewsWithHandler}/{holderCount}",
			$"  retained holders with Forms view reference: {result.HoldersWithFormsView}/{holderCount}",
			$"  retained context payload bytes: {result.RetainedContextPayloadBytes:N0}",
			$"  retained header payload bytes: {result.RetainedHeaderPayloadBytes:N0}",
			$"  retained footer payload bytes: {result.RetainedFooterPayloadBytes:N0}");
	}

	static string FormatBytes(long bytes)
	{
		if (bytes >= 1024 * 1024)
			return $"{bytes / 1024d / 1024d:N1} MiB";
		if (bytes >= 1024)
			return $"{bytes / 1024d:N1} KiB";
		return $"{bytes:N0} B";
	}
}
