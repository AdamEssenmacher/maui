#nullable enable

using System.ComponentModel;
using System.Runtime.CompilerServices;
using Foundation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Maui;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Controls.Handlers.Compatibility;
using Microsoft.Maui.Graphics;
using UIKit;

namespace IosTabbedRendererChildPageSubscriptionRetentionRepro;

internal static class ReproSession
{
	internal const int Cycles = 96;
	internal const int ChildrenPerTabbedPage = 3;
	internal const int PayloadKiBPerContext = 1024;

	const long PayloadBytesPerContext = PayloadKiBPerContext * 1024L;

	static readonly List<IReadOnlyList<Page>> RetainedChildPages = new();

	public static readonly string ResultsPath =
		Path.Combine("/tmp", "ios-tabbedrenderer-child-page-subscription-retention-results.txt");

	public static async Task<ReproReport> RunAsync(IMauiContext context)
	{
		WriteProgress("Starting iOS TabbedRenderer child page subscription retention repro.");
		ForceFullGc();
		var baselineBytes = GC.GetTotalMemory(true);

		WriteProgress("Running control scenario.");
		var control = await RunScenarioAsync(
			"control: remove child Page.PropertyChanged subscriptions before dispose",
			context,
			removeChildPageSubscriptions: true);

		WriteProgress("Running current MAUI scenario.");
		var current = await RunScenarioAsync(
			"current: disposed TabbedRenderer stays subscribed to retained child pages",
			context,
			removeChildPageSubscriptions: false);

		WriteProgress("Finalizing report.");
		ForceFullGc();
		var finalBytes = GC.GetTotalMemory(true);

		GC.KeepAlive(RetainedChildPages);

		return new ReproReport(
			Cycles,
			ChildrenPerTabbedPage,
			PayloadKiBPerContext,
			baselineBytes,
			finalBytes,
			control,
			current);
	}

	static async Task<ScenarioResult> RunScenarioAsync(
		string name,
		IMauiContext baseContext,
		bool removeChildPageSubscriptions)
	{
		var retainedChildren = new List<Page>(Cycles * ChildrenPerTabbedPage);
		var tracked = new List<TrackedCycle>(Cycles);

		for (var i = 0; i < Cycles; i++)
		{
			if (i % 16 == 0)
				WriteProgress($"{name}: cycle {i}/{Cycles}");

			CreateDisposedRendererCycle(i, baseContext, retainedChildren, tracked, removeChildPageSubscriptions);

			if (i % 8 == 0)
				await DrainMainQueueAsync();
		}

		RetainedChildPages.Add(retainedChildren);
		await DrainMainQueueAsync();
		ForceFullGc();

		return ScenarioResult.From(name, retainedChildren, tracked);
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	static void CreateDisposedRendererCycle(
		int cycle,
		IMauiContext baseContext,
		List<Page> retainedChildren,
		List<TrackedCycle> tracked,
		bool removeChildPageSubscriptions)
	{
		using var pool = new NSAutoreleasePool();

		var payloadProvider = new PayloadServiceProvider(baseContext.Services, cycle, PayloadBytesPerContext);
		var cycleContext = new MauiContext(payloadProvider);
		var payload = cycleContext.Services.GetRequiredService<PayloadService>();

		if (payload.Buffer.Length != PayloadBytesPerContext || payload.Touch() == 0)
			throw new InvalidOperationException("The synthetic context payload was not initialized.");

		var tabbedPage = new PayloadTabbedPage(cycle);
		var childPages = new List<Page>(ChildrenPerTabbedPage);

		for (var i = 0; i < ChildrenPerTabbedPage; i++)
		{
			var page = new PayloadChildPage(cycle, i);
			var stubHandler = new StubPageHandler(baseContext);
			page.Handler = stubHandler;
			tabbedPage.Children.Add(page);
			stubHandler.ClearContext();
			childPages.Add(page);
		}

		var renderer = new TabbedRenderer();
		var elementHandler = (IElementHandler)renderer;

		elementHandler.SetMauiContext(cycleContext);
		elementHandler.SetVirtualView(tabbedPage);

		if (!ReferenceEquals(elementHandler.MauiContext, cycleContext))
			throw new InvalidOperationException("TabbedRenderer did not retain the cycle MauiContext.");

		if (!ReferenceEquals(elementHandler.MauiContext.Services.GetRequiredService<PayloadService>(), payload))
			throw new InvalidOperationException("Renderer MauiContext did not resolve the expected payload service.");

		tracked.Add(TrackedCycle.Create(cycle, renderer, tabbedPage, childPages, cycleContext, payloadProvider, payload, payload.Buffer));

		foreach (var page in childPages)
			page.Handler?.DisconnectHandler();

		if (removeChildPageSubscriptions)
		{
			for (var i = 0; i < childPages.Count; i++)
				TeardownPage(renderer, childPages[i], i);
		}

		elementHandler.DisconnectHandler();
		retainedChildren.AddRange(childPages);
	}

	static async Task DrainMainQueueAsync()
	{
		await Task.Delay(50);
		NSRunLoop.Main.RunUntil(NSDate.FromTimeIntervalSinceNow(0.02));
	}

	static void ForceFullGc()
	{
		for (var i = 0; i < 4; i++)
		{
			using var pool = new NSAutoreleasePool();
			NSRunLoop.Current.RunUntil(NSDate.FromTimeIntervalSinceNow(0.01));
			GC.Collect();
			GC.WaitForPendingFinalizers();
			GC.Collect();
			Thread.Sleep(75);
		}
	}

	static void WriteProgress(string message)
	{
		try
		{
			File.WriteAllText(ResultsPath, message + Environment.NewLine);
		}
		catch
		{
			// Progress output is diagnostic only; the final report write remains authoritative.
		}
	}

	static int CountChildPageSubscriptions(IReadOnlyList<Page> pages)
	{
		var count = 0;

		foreach (var page in pages)
		{
			var handler = PropertyChangedField(page);
			if (handler is null)
				continue;

			foreach (var subscriber in handler.GetInvocationList())
			{
				if (subscriber.Target is TabbedRenderer)
					count++;
			}
		}

		return count;
	}

	[UnsafeAccessor(UnsafeAccessorKind.Method, Name = "TeardownPage")]
	static extern void TeardownPage(TabbedRenderer renderer, Page page, int index);

	[UnsafeAccessor(UnsafeAccessorKind.Field, Name = "PropertyChanged")]
	static extern ref PropertyChangedEventHandler? PropertyChangedField(BindableObject bindable);

	[UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_mauiContext")]
	static extern ref IMauiContext? MauiContextField(TabbedRenderer renderer);

	internal sealed class PayloadServiceProvider : IServiceProvider, IKeyedServiceProvider
	{
		readonly IServiceProvider _inner;

		public PayloadServiceProvider(IServiceProvider inner, int cycle, long payloadBytes)
		{
			_inner = inner;
			Payload = new PayloadService(cycle, checked((int)payloadBytes));
		}

		public PayloadService Payload { get; }

		public object? GetService(Type serviceType)
		{
			if (serviceType == typeof(PayloadService))
				return Payload;

			return _inner.GetService(serviceType);
		}

		public object? GetKeyedService(Type serviceType, object? serviceKey)
		{
			if (serviceType == typeof(PayloadService))
				return Payload;

			return _inner is IKeyedServiceProvider keyedProvider
				? keyedProvider.GetKeyedService(serviceType, serviceKey)
				: null;
		}

		public object GetRequiredKeyedService(Type serviceType, object? serviceKey)
		{
			if (serviceType == typeof(PayloadService))
				return Payload;

			if (_inner is IKeyedServiceProvider keyedProvider)
				return keyedProvider.GetRequiredKeyedService(serviceType, serviceKey);

			throw new InvalidOperationException($"No keyed service provider is available for {serviceType}.");
		}
	}

	internal sealed class PayloadService
	{
		public PayloadService(int cycle, int payloadBytes)
		{
			Cycle = cycle;
			Buffer = new byte[payloadBytes];

			for (var i = 0; i < Buffer.Length; i += 4096)
				Buffer[i] = unchecked((byte)(cycle + i));
		}

		public int Cycle { get; }

		public byte[] Buffer { get; }

		public int Touch()
		{
			var checksum = Cycle + 1;

			for (var i = 0; i < Buffer.Length; i += 4096)
				checksum += Buffer[i] + 1;

			return checksum;
		}
	}

	internal sealed record TrackedCycle(
		int Cycle,
		WeakReference<TabbedRenderer> Renderer,
		WeakReference<TabbedPage> TabbedPage,
		IReadOnlyList<WeakReference<Page>> ChildPages,
		WeakReference<IMauiContext> Context,
		WeakReference<PayloadServiceProvider> Provider,
		WeakReference<PayloadService> Payload,
		WeakReference<byte[]> PayloadBuffer)
	{
		public static TrackedCycle Create(
			int cycle,
			TabbedRenderer renderer,
			TabbedPage tabbedPage,
			IReadOnlyList<Page> childPages,
			IMauiContext context,
			PayloadServiceProvider provider,
			PayloadService payload,
			byte[] payloadBuffer)
		{
			return new TrackedCycle(
				cycle,
				new WeakReference<TabbedRenderer>(renderer),
				new WeakReference<TabbedPage>(tabbedPage),
				childPages.Select(static page => new WeakReference<Page>(page)).ToArray(),
				new WeakReference<IMauiContext>(context),
				new WeakReference<PayloadServiceProvider>(provider),
				new WeakReference<PayloadService>(payload),
				new WeakReference<byte[]>(payloadBuffer));
		}
	}

	internal sealed record ScenarioResult(
		string Name,
		int TrackedCycles,
		int RetainedChildPages,
		int ChildPageSubscriptionsToTabbedRenderer,
		int AliveRenderers,
		int RenderersWithMauiContext,
		int RenderersResolvingPayloads,
		long EstimatedContextPayloadBytes,
		int AliveTabbedPages,
		int AliveChildPages,
		int AliveContexts,
		int AliveProviders,
		int AlivePayloads,
		int AlivePayloadBuffers)
	{
		internal static ScenarioResult From(
			string name,
			IReadOnlyList<Page> retainedChildPages,
			IReadOnlyList<TrackedCycle> tracked)
		{
			var childPageSubscriptions = CountChildPageSubscriptions(retainedChildPages);
			var aliveRenderers = 0;
			var renderersWithMauiContext = 0;
			var renderersResolvingPayloads = 0;
			long estimatedContextPayloadBytes = 0;
			var aliveTabbedPages = 0;
			var aliveChildPages = 0;
			var aliveContexts = 0;
			var aliveProviders = 0;
			var alivePayloads = 0;
			var alivePayloadBuffers = 0;

			foreach (var cycle in tracked)
			{
				if (cycle.Renderer.TryGetTarget(out var renderer))
				{
					aliveRenderers++;

					var rendererContext = MauiContextField(renderer);
					if (rendererContext is not null)
					{
						renderersWithMauiContext++;

						if (rendererContext.Services.GetService(typeof(PayloadService)) is PayloadService payload)
						{
							renderersResolvingPayloads++;
							estimatedContextPayloadBytes += Math.Min(payload.Buffer.Length, PayloadBytesPerContext);
						}
					}
				}

				if (cycle.TabbedPage.TryGetTarget(out _))
					aliveTabbedPages++;

				foreach (var childPage in cycle.ChildPages)
				{
					if (childPage.TryGetTarget(out _))
						aliveChildPages++;
				}

				if (cycle.Context.TryGetTarget(out _))
					aliveContexts++;

				if (cycle.Provider.TryGetTarget(out _))
					aliveProviders++;

				if (cycle.Payload.TryGetTarget(out _))
					alivePayloads++;

				if (cycle.PayloadBuffer.TryGetTarget(out _))
					alivePayloadBuffers++;
			}

			return new ScenarioResult(
				name,
				tracked.Count,
				retainedChildPages.Count,
				childPageSubscriptions,
				aliveRenderers,
				renderersWithMauiContext,
				renderersResolvingPayloads,
				estimatedContextPayloadBytes,
				aliveTabbedPages,
				aliveChildPages,
				aliveContexts,
				aliveProviders,
				alivePayloads,
				alivePayloadBuffers);
		}
	}
}

sealed class PayloadTabbedPage : TabbedPage
{
	public PayloadTabbedPage(int cycle)
	{
		Title = $"Regional operations tabs {cycle + 1}";
		AutomationId = $"tabbed-renderer-child-subscription-{cycle + 1}";
		BarBackgroundColor = Colors.White;
		BarTextColor = Colors.Black;
	}
}

sealed class PayloadChildPage : ContentPage
{
	public PayloadChildPage(int cycle, int child)
	{
		Title = $"Territory {cycle + 1}-{child + 1}";
		AutomationId = $"retained-tab-child-{cycle + 1}-{child + 1}";
		Content = new Label { Text = Title };
	}
}

sealed class StubPageHandler : IPlatformViewHandler, IDisposable
{
	readonly UIViewController _viewController = new();
	IMauiContext? _mauiContext;
	IView? _virtualView;
	bool _disposed;

	public StubPageHandler(IMauiContext mauiContext)
	{
		_mauiContext = mauiContext;
	}

	public bool HasContainer { get; set; }

	public UIView? PlatformView => _viewController.View;

	object? IElementHandler.PlatformView => PlatformView;

	object? IViewHandler.ContainerView => null;

	UIView? IPlatformViewHandler.ContainerView => null;

	public UIViewController? ViewController => _viewController;

	public IView? VirtualView => _virtualView;

	IElement? IElementHandler.VirtualView => _virtualView;

	public IMauiContext? MauiContext => _mauiContext;

	public void ClearContext()
	{
		_mauiContext = null;
	}

	public void SetMauiContext(IMauiContext mauiContext)
	{
	}

	public void SetVirtualView(IElement view)
	{
		_virtualView = (IView)view;

		if (view.Handler != this)
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
		var view = _virtualView;
		_virtualView = null;

		if (view?.Handler == this)
			view.Handler = null;
	}

	public Size GetDesiredSize(double widthConstraint, double heightConstraint) => Size.Zero;

	public void PlatformArrange(Rect frame)
	{
	}

	public void Dispose()
	{
		if (_disposed)
			return;

		_disposed = true;
		DisconnectHandler();
		_viewController.Dispose();
	}
}

internal sealed record ReproReport(
	int Cycles,
	int ChildrenPerTabbedPage,
	int PayloadKiBPerContext,
	long BaselineManagedBytes,
	long FinalManagedBytes,
	ReproSession.ScenarioResult Control,
	ReproSession.ScenarioResult Current)
{
	public bool LeakProved =>
		Control.RetainedChildPages == Cycles * ChildrenPerTabbedPage &&
		Control.ChildPageSubscriptionsToTabbedRenderer == 0 &&
		Control.AliveRenderers <= 1 &&
		Control.RenderersWithMauiContext == 0 &&
		Control.RenderersResolvingPayloads == 0 &&
		Control.AliveContexts <= 1 &&
		Control.AliveProviders <= 1 &&
		Control.AlivePayloads <= 1 &&
		Control.AlivePayloadBuffers <= 1 &&
		Control.AliveChildPages == Cycles * ChildrenPerTabbedPage &&
		Current.RetainedChildPages == Cycles * ChildrenPerTabbedPage &&
		Current.ChildPageSubscriptionsToTabbedRenderer == Cycles * ChildrenPerTabbedPage &&
		Current.AliveRenderers == Cycles &&
		Current.RenderersWithMauiContext == Cycles &&
		Current.RenderersResolvingPayloads == Cycles &&
		Current.AliveContexts == Cycles &&
		Current.AliveProviders == Cycles &&
		Current.AlivePayloads == Cycles &&
		Current.AlivePayloadBuffers == Cycles &&
		Current.AliveChildPages == Cycles * ChildrenPerTabbedPage &&
		Current.EstimatedContextPayloadBytes >= Cycles * PayloadKiBPerContext * 1024L * 0.95;

	public string ToText()
	{
		var currentMiB = Current.EstimatedContextPayloadBytes / 1024d / 1024d;
		var controlMiB = Control.EstimatedContextPayloadBytes / 1024d / 1024d;
		var heapDeltaMiB = (FinalManagedBytes - BaselineManagedBytes) / 1024d / 1024d;

		return string.Join(Environment.NewLine,
			"IosTabbedRendererChildPageSubscriptionRetentionRepro",
			$"Cycles: {Cycles}",
			$"Children per TabbedPage retained in both runs: {ChildrenPerTabbedPage}",
			$"Payload per MauiContext: {PayloadKiBPerContext} KiB",
			$"Baseline managed heap: {BaselineManagedBytes:N0} bytes",
			$"Final managed heap: {FinalManagedBytes:N0} bytes",
			$"Managed heap delta: {heapDeltaMiB:N1} MiB",
			$"Leak proved: {LeakProved}",
			string.Empty,
			Format(Control, ChildrenPerTabbedPage),
			string.Empty,
			Format(Current, ChildrenPerTabbedPage),
			string.Empty,
			$"Control estimated retained context payload: {controlMiB:N1} MiB",
			$"Current estimated retained context payload: {currentMiB:N1} MiB",
			$"RESULT: {(LeakProved ? "PROVEN" : "NOT PROVEN")}");
	}

	static string Format(ReproSession.ScenarioResult result, int childrenPerTabbedPage)
	{
		var payloadMiB = result.EstimatedContextPayloadBytes / 1024d / 1024d;

		return string.Join(Environment.NewLine,
			$"Run: {result.Name}",
			$"  tracked cycles: {result.TrackedCycles}",
			$"  app-retained child pages: {result.RetainedChildPages}/{result.TrackedCycles * childrenPerTabbedPage}",
			$"  child page subscriptions to TabbedRenderer: {result.ChildPageSubscriptionsToTabbedRenderer}/{result.TrackedCycles * childrenPerTabbedPage}",
			$"  alive TabbedRenderers: {result.AliveRenderers}/{result.TrackedCycles}",
			$"  renderers with retained MauiContext: {result.RenderersWithMauiContext}/{result.TrackedCycles}",
			$"  renderers resolving payload service: {result.RenderersResolvingPayloads}/{result.TrackedCycles}",
			$"  estimated retained context payload bytes: {result.EstimatedContextPayloadBytes:N0}",
			$"  estimated retained context payload MiB: {payloadMiB:N1}",
			$"  alive TabbedPages: {result.AliveTabbedPages}/{result.TrackedCycles}",
			$"  alive child pages: {result.AliveChildPages}/{result.TrackedCycles * childrenPerTabbedPage}",
			$"  alive MauiContexts: {result.AliveContexts}/{result.TrackedCycles}",
			$"  alive payload service providers: {result.AliveProviders}/{result.TrackedCycles}",
			$"  alive payload services: {result.AlivePayloads}/{result.TrackedCycles}",
			$"  alive payload byte arrays: {result.AlivePayloadBuffers}/{result.TrackedCycles}");
	}
}
