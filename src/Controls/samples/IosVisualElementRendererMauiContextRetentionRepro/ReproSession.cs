#nullable enable

using System.Runtime.CompilerServices;
using Foundation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Maui;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Controls.Handlers.Compatibility;
using ObjCRuntime;

#pragma warning disable CS0618

namespace IosVisualElementRendererMauiContextRetentionRepro;

internal static class ReproSession
{
	internal const int Cycles = 96;
	internal const int PayloadKiBPerContext = 1024;

	const long PayloadBytesPerContext = PayloadKiBPerContext * 1024L;

	static readonly List<IReadOnlyList<FrameRenderer>> RetainedRenderers = new();

	public static readonly string ResultsPath =
		Path.Combine("/tmp", "ios-visualelementrenderer-mauicontext-retention-results.txt");

	public static async Task<ReproReport> RunAsync(IMauiContext context)
	{
		WriteProgress("Starting iOS VisualElementRenderer MauiContext retention repro.");
		ForceFullGc();
		var baselineBytes = GC.GetTotalMemory(true);

		WriteProgress("Running control scenario.");
		var control = await RunScenarioAsync(
			"control: clear retained VisualElementRenderer<Frame>._mauiContext",
			context,
			clearRendererContext: true);

		WriteProgress("Running current MAUI scenario.");
		var current = await RunScenarioAsync(
			"current: VisualElementRenderer<Frame> keeps _mauiContext after disconnect/dispose",
			context,
			clearRendererContext: false);

		WriteProgress("Finalizing report.");
		ForceFullGc();
		var finalBytes = GC.GetTotalMemory(true);

		GC.KeepAlive(RetainedRenderers);

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
		bool clearRendererContext)
	{
		var renderers = new List<FrameRenderer>(Cycles);
		var tracked = new List<TrackedCycle>(Cycles);

		for (var i = 0; i < Cycles; i++)
		{
			if (i % 16 == 0)
				WriteProgress($"{name}: cycle {i}/{Cycles}");

			CreateDisposedRendererCycle(i, baseContext, renderers, tracked, clearRendererContext);

			if (i % 8 == 0)
				await DrainMainQueueAsync();
		}

		RetainedRenderers.Add(renderers);
		await DrainMainQueueAsync();
		ForceFullGc();

		return ScenarioResult.From(name, renderers, tracked);
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	static void CreateDisposedRendererCycle(
		int cycle,
		IMauiContext baseContext,
		List<FrameRenderer> renderers,
		List<TrackedCycle> tracked,
		bool clearRendererContext)
	{
		using var pool = new NSAutoreleasePool();

		var payloadProvider = new PayloadServiceProvider(baseContext.Services, cycle, PayloadBytesPerContext);
		var cycleContext = new MauiContext(payloadProvider);
		var payload = cycleContext.Services.GetRequiredService<PayloadService>();

		if (payload.Buffer.Length != PayloadBytesPerContext || payload.Touch() == 0)
			throw new InvalidOperationException("The synthetic context payload was not initialized.");

		var frame = new PayloadFrame(cycle);
		var renderer = new FrameRenderer();
		var elementHandler = (IElementHandler)renderer;

		elementHandler.SetMauiContext(cycleContext);
		elementHandler.SetVirtualView(frame);

		if (!ReferenceEquals(elementHandler.MauiContext, cycleContext))
			throw new InvalidOperationException("FrameRenderer did not retain the cycle MauiContext.");

		if (!ReferenceEquals(elementHandler.MauiContext.Services.GetRequiredService<PayloadService>(), payload))
			throw new InvalidOperationException("Renderer MauiContext did not resolve the expected payload service.");

		tracked.Add(TrackedCycle.Create(cycle, renderer, frame, cycleContext, payloadProvider, payload, payload.Buffer));

		elementHandler.DisconnectHandler();
		renderer.Dispose();

		if (clearRendererContext)
			MauiContextField(renderer) = null;

		renderers.Add(renderer);
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

	[UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_mauiContext")]
	static extern ref IMauiContext? MauiContextField(VisualElementRenderer<Frame> renderer);

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
		WeakReference<FrameRenderer> Renderer,
		WeakReference<Frame> Frame,
		WeakReference<IMauiContext> Context,
		WeakReference<PayloadServiceProvider> Provider,
		WeakReference<PayloadService> Payload,
		WeakReference<byte[]> PayloadBuffer)
	{
		public static TrackedCycle Create(
			int cycle,
			FrameRenderer renderer,
			Frame frame,
			IMauiContext context,
			PayloadServiceProvider provider,
			PayloadService payload,
			byte[] payloadBuffer)
		{
			return new TrackedCycle(
				cycle,
				new WeakReference<FrameRenderer>(renderer),
				new WeakReference<Frame>(frame),
				new WeakReference<IMauiContext>(context),
				new WeakReference<PayloadServiceProvider>(provider),
				new WeakReference<PayloadService>(payload),
				new WeakReference<byte[]>(payloadBuffer));
		}
	}

	internal sealed record ScenarioResult(
		string Name,
		int TrackedCycles,
		int RetainedRendererPeers,
		int RenderersWithMauiContext,
		int RenderersResolvingPayloads,
		long EstimatedContextPayloadBytes,
		int AliveRenderers,
		int AliveFrames,
		int AliveContexts,
		int AliveProviders,
		int AlivePayloads,
		int AlivePayloadBuffers)
	{
		internal static ScenarioResult From(
			string name,
			IReadOnlyList<FrameRenderer> renderers,
			IReadOnlyList<TrackedCycle> tracked)
		{
			var renderersWithMauiContext = 0;
			var renderersResolvingPayloads = 0;
			long estimatedContextPayloadBytes = 0;

			foreach (var renderer in renderers)
			{
				var rendererContext = MauiContextField(renderer);
				if (rendererContext is null)
					continue;

				renderersWithMauiContext++;

				if (rendererContext.Services.GetService(typeof(PayloadService)) is PayloadService payload)
				{
					renderersResolvingPayloads++;
					estimatedContextPayloadBytes += Math.Min(payload.Buffer.Length, PayloadBytesPerContext);
				}
			}

			var aliveRenderers = 0;
			var aliveFrames = 0;
			var aliveContexts = 0;
			var aliveProviders = 0;
			var alivePayloads = 0;
			var alivePayloadBuffers = 0;

			foreach (var cycle in tracked)
			{
				if (cycle.Renderer.TryGetTarget(out _))
					aliveRenderers++;

				if (cycle.Frame.TryGetTarget(out _))
					aliveFrames++;

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
				renderers.Count,
				renderersWithMauiContext,
				renderersResolvingPayloads,
				estimatedContextPayloadBytes,
				aliveRenderers,
				aliveFrames,
				aliveContexts,
				aliveProviders,
				alivePayloads,
				alivePayloadBuffers);
		}
	}
}

sealed class PayloadFrame : Frame
{
	public PayloadFrame(int cycle)
	{
		AutomationId = $"visual-element-renderer-context-{cycle + 1}";
		BorderColor = Colors.SteelBlue;
		BackgroundColor = Colors.White;
		CornerRadius = 8;
		HasShadow = false;
		Padding = new Thickness(12);
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
		Control.RetainedRendererPeers == Cycles &&
		Control.AliveRenderers == Cycles &&
		Control.RenderersWithMauiContext == 0 &&
		Control.RenderersResolvingPayloads == 0 &&
		Control.AliveContexts <= 1 &&
		Control.AliveProviders <= 1 &&
		Control.AlivePayloads <= 1 &&
		Control.AlivePayloadBuffers <= 1 &&
		Control.AliveFrames <= 1 &&
		Current.RetainedRendererPeers == Cycles &&
		Current.AliveRenderers == Cycles &&
		Current.RenderersWithMauiContext == Cycles &&
		Current.RenderersResolvingPayloads == Cycles &&
		Current.AliveContexts == Cycles &&
		Current.AliveProviders == Cycles &&
		Current.AlivePayloads == Cycles &&
		Current.AlivePayloadBuffers == Cycles &&
		Current.EstimatedContextPayloadBytes >= Cycles * PayloadKiBPerContext * 1024L * 0.95 &&
		Current.AliveFrames <= 1;

	public string ToText()
	{
		var currentMiB = Current.EstimatedContextPayloadBytes / 1024d / 1024d;
		var controlMiB = Control.EstimatedContextPayloadBytes / 1024d / 1024d;
		var heapDeltaMiB = (FinalManagedBytes - BaselineManagedBytes) / 1024d / 1024d;

		return string.Join(Environment.NewLine,
			"IosVisualElementRendererMauiContextRetentionRepro",
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
			$"  retained renderer peers: {result.RetainedRendererPeers}/{result.TrackedCycles}",
			$"  renderers with retained MauiContext: {result.RenderersWithMauiContext}/{result.TrackedCycles}",
			$"  renderers resolving payload service: {result.RenderersResolvingPayloads}/{result.TrackedCycles}",
			$"  estimated retained context payload bytes: {result.EstimatedContextPayloadBytes:N0}",
			$"  estimated retained context payload MiB: {payloadMiB:N1}",
			$"  alive renderers: {result.AliveRenderers}/{result.TrackedCycles}",
			$"  alive Frames: {result.AliveFrames}/{result.TrackedCycles}",
			$"  alive MauiContexts: {result.AliveContexts}/{result.TrackedCycles}",
			$"  alive payload service providers: {result.AliveProviders}/{result.TrackedCycles}",
			$"  alive payload services: {result.AlivePayloads}/{result.TrackedCycles}",
			$"  alive payload byte arrays: {result.AlivePayloadBuffers}/{result.TrackedCycles}");
	}
}
