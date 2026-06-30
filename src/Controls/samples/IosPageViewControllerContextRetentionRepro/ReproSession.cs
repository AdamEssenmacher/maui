#nullable enable

using System.Runtime.InteropServices;
using Foundation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Maui;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Handlers;
using Microsoft.Maui.Platform;
using ObjCRuntime;
using UIKit;

namespace IosPageViewControllerContextRetentionRepro;

internal static class ReproSession
{
	internal const int Cycles = 96;
	internal const int PayloadKiBPerContext = 1024;

	const long PayloadBytesPerContext = PayloadKiBPerContext * 1024L;

	static readonly IntPtr RetainSelector = Selector.GetHandle("retain");
	static readonly List<IReadOnlyList<RetainedNativeViewController>> RetainedNativeViewControllers = new();

	public static readonly string ResultsPath =
		Path.Combine("/tmp", "ios-pageviewcontroller-context-retention-results.txt");

	public static async Task<ReproReport> RunAsync(IMauiContext context)
	{
		WriteProgress("Starting iOS PageViewController context retention repro.");
		ForceFullGc();
		var baselineBytes = GC.GetTotalMemory(true);

		WriteProgress("Running control scenario.");
		var control = await RunScenarioAsync(
			"control: clear retained ContainerViewController.Context",
			context,
			clearControllerContext: true);

		WriteProgress("Running current MAUI scenario.");
		var current = await RunScenarioAsync(
			"current: PageViewController keeps Context after handler disconnect",
			context,
			clearControllerContext: false);

		WriteProgress("Finalizing report.");
		ForceFullGc();
		var finalBytes = GC.GetTotalMemory(true);

		GC.KeepAlive(RetainedNativeViewControllers);

		return new ReproReport(
			Cycles,
			PayloadKiBPerContext,
			baselineBytes,
			finalBytes,
			control,
			current);
	}

	static async Task<ScenarioResult> RunScenarioAsync(
		string name,
		IMauiContext baseContext,
		bool clearControllerContext)
	{
		var retainedControllers = new List<RetainedNativeViewController>(Cycles);
		var tracked = new List<TrackedCycle>(Cycles);

		for (var i = 0; i < Cycles; i++)
		{
			if (i % 16 == 0)
				WriteProgress($"{name}: cycle {i}/{Cycles}");

			var cycleResult = await RunCycleAsync(i, baseContext, clearControllerContext);
			retainedControllers.Add(cycleResult.RetainedViewController);
			tracked.Add(cycleResult.Tracked);
		}

		RetainedNativeViewControllers.Add(retainedControllers);
		ForceFullGc();

		return ScenarioResult.From(name, retainedControllers, tracked);
	}

	static async Task<CycleResult> RunCycleAsync(
		int cycle,
		IMauiContext baseContext,
		bool clearControllerContext)
	{
		var payloadProvider = new PayloadServiceProvider(baseContext.Services, cycle, PayloadBytesPerContext);
		var cycleContext = new MauiContext(payloadProvider);
		var payload = cycleContext.Services.GetRequiredService<PayloadService>();

		if (payload.Buffer.Length != PayloadBytesPerContext || payload.Touch() == 0)
			throw new InvalidOperationException("The synthetic context payload was not initialized.");

		var content = new Label
		{
			Text = $"Generated workflow page {cycle:0000}"
		};

		var page = new ContentPage
		{
			Title = string.Empty,
			Content = content
		};

		var handler = page.ToHandler(cycleContext);

		if (handler is not IPlatformViewHandler platformViewHandler || platformViewHandler.ViewController is not ContainerViewController viewController)
			throw new InvalidOperationException("ContentPage handler did not expose a ContainerViewController.");

		if (!ReferenceEquals(viewController.Context, cycleContext))
			throw new InvalidOperationException("PageViewController did not retain the cycle MauiContext.");

		if (!ReferenceEquals(viewController.Context.Services.GetRequiredService<PayloadService>(), payload))
			throw new InvalidOperationException("PageViewController.Context did not resolve the expected payload service.");

		var retainedViewController = RetainNativeViewController(viewController);
		var tracked = TrackedCycle.Create(cycle, page, content, handler, cycleContext, payloadProvider, payload, payload.Buffer);

		handler.DisconnectHandler();

		if (clearControllerContext)
			viewController.Context = null;

		await DrainMainQueueAsync();

		return new CycleResult(retainedViewController, tracked);
	}

	static RetainedNativeViewController RetainNativeViewController(UIViewController viewController)
	{
		var handle = viewController.Handle;
		if (handle == IntPtr.Zero)
			throw new InvalidOperationException("Cannot retain a native UIViewController with a zero handle.");

		var retained = IntPtr_objc_msgSend(handle, RetainSelector);
		if (retained == IntPtr.Zero)
			throw new InvalidOperationException("Objective-C retain returned a zero handle.");

		return new RetainedNativeViewController(retained);
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

	[DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
	static extern IntPtr IntPtr_objc_msgSend(IntPtr receiver, IntPtr selector);

	internal sealed record CycleResult(RetainedNativeViewController RetainedViewController, TrackedCycle Tracked);

	internal sealed class RetainedNativeViewController
	{
		public RetainedNativeViewController(IntPtr handle)
		{
			Handle = handle;
		}

		public IntPtr Handle { get; }

		public UIViewController? TryGetViewController()
		{
			if (Handle == IntPtr.Zero)
				return null;

			try
			{
				return Runtime.GetNSObject<UIViewController>(Handle, false);
			}
			catch
			{
				return null;
			}
		}
	}

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
		WeakReference<ContentPage> Page,
		WeakReference<Label> Content,
		WeakReference<IElementHandler> Handler,
		WeakReference<IMauiContext> Context,
		WeakReference<PayloadServiceProvider> Provider,
		WeakReference<PayloadService> Payload,
		WeakReference<byte[]> PayloadBuffer)
	{
		public static TrackedCycle Create(
			int cycle,
			ContentPage page,
			Label content,
			IElementHandler handler,
			IMauiContext context,
			PayloadServiceProvider provider,
			PayloadService payload,
			byte[] payloadBuffer)
		{
			return new TrackedCycle(
				cycle,
				new WeakReference<ContentPage>(page),
				new WeakReference<Label>(content),
				new WeakReference<IElementHandler>(handler),
				new WeakReference<IMauiContext>(context),
				new WeakReference<PayloadServiceProvider>(provider),
				new WeakReference<PayloadService>(payload),
				new WeakReference<byte[]>(payloadBuffer));
		}
	}

	internal sealed record ScenarioResult(
		string Name,
		int TrackedCycles,
		int RetainedNativeViewControllers,
		int ViewControllersWithContexts,
		int ViewControllersResolvingPayloads,
		long EstimatedContextPayloadBytes,
		int AlivePages,
		int AliveContent,
		int AliveHandlers,
		int AliveContexts,
		int AliveProviders,
		int AlivePayloads,
		int AlivePayloadBuffers)
	{
		internal static ScenarioResult From(
			string name,
			IReadOnlyList<RetainedNativeViewController> retainedViewControllers,
			IReadOnlyList<TrackedCycle> tracked)
		{
			var retainedNativeViewControllers = 0;
			var viewControllersWithContexts = 0;
			var viewControllersResolvingPayloads = 0;
			long estimatedContextPayloadBytes = 0;

			foreach (var retainedViewController in retainedViewControllers)
			{
				var viewController = retainedViewController.TryGetViewController();
				if (viewController is null)
					continue;

				retainedNativeViewControllers++;

				if (viewController is ContainerViewController { Context: { } controllerContext })
				{
					viewControllersWithContexts++;

					if (controllerContext.Services.GetService(typeof(PayloadService)) is PayloadService payload)
					{
						viewControllersResolvingPayloads++;
						estimatedContextPayloadBytes += Math.Min(payload.Buffer.Length, PayloadBytesPerContext);
					}
				}
			}

			var alivePages = 0;
			var aliveContent = 0;
			var aliveHandlers = 0;
			var aliveContexts = 0;
			var aliveProviders = 0;
			var alivePayloads = 0;
			var alivePayloadBuffers = 0;

			foreach (var cycle in tracked)
			{
				if (cycle.Page.TryGetTarget(out _))
					alivePages++;

				if (cycle.Content.TryGetTarget(out _))
					aliveContent++;

				if (cycle.Handler.TryGetTarget(out _))
					aliveHandlers++;

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
				retainedNativeViewControllers,
				viewControllersWithContexts,
				viewControllersResolvingPayloads,
				estimatedContextPayloadBytes,
				alivePages,
				aliveContent,
				aliveHandlers,
				aliveContexts,
				aliveProviders,
				alivePayloads,
				alivePayloadBuffers);
		}
	}
}

internal sealed record ReproReport(
	int Cycles,
	int PayloadKiBPerContext,
	long BaselineManagedBytes,
	long FinalManagedBytes,
	ReproSession.ScenarioResult Control,
	ReproSession.ScenarioResult Current)
{
	public bool LeakProved =>
		Control.RetainedNativeViewControllers == Cycles &&
		Control.ViewControllersWithContexts == 0 &&
		Control.ViewControllersResolvingPayloads == 0 &&
		Control.AliveContexts <= 1 &&
		Control.AlivePayloadBuffers <= 1 &&
		Current.RetainedNativeViewControllers == Cycles &&
		Current.ViewControllersWithContexts == Cycles &&
		Current.ViewControllersResolvingPayloads == Cycles &&
		Current.AliveContexts == Cycles &&
		Current.AliveProviders == Cycles &&
		Current.AlivePayloads == Cycles &&
		Current.AlivePayloadBuffers == Cycles &&
		Current.EstimatedContextPayloadBytes >= Cycles * PayloadKiBPerContext * 1024L * 0.95 &&
		Current.AlivePages <= 1 &&
		Current.AliveContent <= 1 &&
		Current.AliveHandlers <= 1;

	public string ToText()
	{
		var currentMiB = Current.EstimatedContextPayloadBytes / 1024d / 1024d;
		var controlMiB = Control.EstimatedContextPayloadBytes / 1024d / 1024d;
		var heapDeltaMiB = (FinalManagedBytes - BaselineManagedBytes) / 1024d / 1024d;

		return string.Join(Environment.NewLine,
			"IosPageViewControllerContextRetentionRepro",
			$"Cycles: {Cycles}",
			$"Payload per MauiContext: {PayloadKiBPerContext} KiB",
			$"Baseline managed heap: {BaselineManagedBytes:N0} bytes",
			$"Final managed heap: {FinalManagedBytes:N0} bytes",
			$"Managed heap delta: {heapDeltaMiB:N1} MiB",
			$"Leak proved: {LeakProved}",
			string.Empty,
			Format(Control),
			string.Empty,
			Format(Current),
			string.Empty,
			$"Control estimated retained context payload: {controlMiB:N1} MiB",
			$"Current estimated retained context payload: {currentMiB:N1} MiB",
			$"RESULT: {(LeakProved ? "PROVEN" : "NOT PROVEN")}");
	}

	static string Format(ReproSession.ScenarioResult result)
	{
		var payloadMiB = result.EstimatedContextPayloadBytes / 1024d / 1024d;

		return string.Join(Environment.NewLine,
			$"Run: {result.Name}",
			$"  tracked cycles: {result.TrackedCycles}",
			$"  retained native view controllers: {result.RetainedNativeViewControllers}/{result.TrackedCycles}",
			$"  view controllers with retained Context: {result.ViewControllersWithContexts}/{result.TrackedCycles}",
			$"  view controllers resolving payload service: {result.ViewControllersResolvingPayloads}/{result.TrackedCycles}",
			$"  estimated retained context payload bytes: {result.EstimatedContextPayloadBytes:N0}",
			$"  estimated retained context payload MiB: {payloadMiB:N1}",
			$"  alive pages: {result.AlivePages}/{result.TrackedCycles}",
			$"  alive content views: {result.AliveContent}/{result.TrackedCycles}",
			$"  alive handlers: {result.AliveHandlers}/{result.TrackedCycles}",
			$"  alive MauiContexts: {result.AliveContexts}/{result.TrackedCycles}",
			$"  alive payload service providers: {result.AliveProviders}/{result.TrackedCycles}",
			$"  alive payload services: {result.AlivePayloads}/{result.TrackedCycles}",
			$"  alive payload byte arrays: {result.AlivePayloadBuffers}/{result.TrackedCycles}");
	}
}
