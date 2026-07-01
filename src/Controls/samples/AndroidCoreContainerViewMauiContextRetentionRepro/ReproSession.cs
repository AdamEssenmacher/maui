#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Android.Runtime;
using Microsoft.Maui;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Graphics;
using CoreContainerView = Microsoft.Maui.Platform.ContainerView;

namespace AndroidCoreContainerViewMauiContextRetentionRepro;

internal static class ReproSession
{
	const int Attempts = 96;
	const int PayloadBytes = 1024 * 1024;

	static readonly FieldInfo ContainerContextField =
		typeof(CoreContainerView).GetField("_context", BindingFlags.Instance | BindingFlags.NonPublic)
		?? throw new MissingFieldException(typeof(CoreContainerView).FullName, "_context");

	static readonly List<object> RetainedNativePeerRoots = new();

	public static async Task<ReproReport> RunAsync(IMauiContext hostContext)
	{
		await Task.Yield();

		RetainedNativePeerRoots.Clear();
		ForceFullGc();
		var baseline = GC.GetTotalMemory(forceFullCollection: true);

		var control = await RunScenarioAsync(
			hostContext,
			"control: clear core ContainerView._context after CurrentView cleanup",
			clearContainerContext: true);

		var current = await RunScenarioAsync(
			hostContext,
			"current: core ContainerView cleanup leaves _context assigned",
			clearContainerContext: false);

		ForceFullGc();
		var final = GC.GetTotalMemory(forceFullCollection: true);
		GC.KeepAlive(RetainedNativePeerRoots);

		return new ReproReport(Attempts, PayloadBytes, control, current, baseline, final);
	}

	static async Task<RunStats> RunScenarioAsync(
		IMauiContext hostContext,
		string name,
		bool clearContainerContext)
	{
		var retainedNativeContainers = new List<NativePeerRoot>(Attempts);
		var containerRefs = new List<WeakReference<CoreContainerView>>(Attempts);
		var viewRefs = new List<WeakReference<BoxView>>(Attempts);
		var handlerRefs = new List<WeakReference<IElementHandler>>(Attempts);
		var contextRefs = new List<WeakReference<IMauiContext>>(Attempts);
		var providerRefs = new List<WeakReference<PayloadServiceProvider>>(Attempts);
		var payloadRefs = new List<PayloadWeakReference>(Attempts);

		for (var i = 0; i < Attempts; i++)
		{
			CreateClearedContainer(
				hostContext,
				clearContainerContext,
				retainedNativeContainers,
				containerRefs,
				viewRefs,
				handlerRefs,
				contextRefs,
				providerRefs,
				payloadRefs,
				i);

			if (i % 12 == 0)
				await Task.Yield();
		}

		RetainedNativePeerRoots.Add(retainedNativeContainers);
		ForceFullGc();
		GC.KeepAlive(retainedNativeContainers);

		var aliveContainers = containerRefs.Count(static wr => wr.TryGetTarget(out _));
		var aliveViews = viewRefs.Count(static wr => wr.TryGetTarget(out _));
		var aliveHandlers = handlerRefs.Count(static wr => wr.TryGetTarget(out _));
		var aliveContexts = contextRefs.Count(static wr => wr.TryGetTarget(out _));
		var aliveProviders = providerRefs.Count(static wr => wr.TryGetTarget(out _));
		var alivePayloadServices = payloadRefs.Count(static wr => wr.PayloadService.TryGetTarget(out _));
		var alivePayloadByteArrays = payloadRefs.Count(static wr => wr.Bytes.TryGetTarget(out _));
		var containersWithContext = containerRefs.Count(static wr =>
			wr.TryGetTarget(out var container) &&
			ContainerContextField.GetValue(container) is IMauiContext);
		var containersResolvingPayloadService = containerRefs.Count(static wr =>
			wr.TryGetTarget(out var container) &&
			ContainerContextField.GetValue(container) is IMauiContext context &&
			context.Services.GetService(typeof(PayloadService)) is PayloadService);
		var containersWithCurrentView = containerRefs.Count(static wr =>
			wr.TryGetTarget(out var container) &&
			container.CurrentView is not null);
		var containersWithMainView = containerRefs.Count(static wr =>
			wr.TryGetTarget(out var container) &&
			container.MainView is not null);
		var retainedNativeGlobalRefs = retainedNativeContainers.Count(static root => root.GlobalRef != IntPtr.Zero);

		return new RunStats(
			name,
			Attempts,
			retainedNativeGlobalRefs,
			aliveContainers,
			aliveViews,
			aliveHandlers,
			aliveContexts,
			aliveProviders,
			alivePayloadServices,
			alivePayloadByteArrays,
			containersWithContext,
			containersResolvingPayloadService,
			containersWithCurrentView,
			containersWithMainView,
			(long)alivePayloadByteArrays * PayloadBytes);
	}

	static void CreateClearedContainer(
		IMauiContext hostContext,
		bool clearContainerContext,
		List<NativePeerRoot> retainedNativeContainers,
		List<WeakReference<CoreContainerView>> containerRefs,
		List<WeakReference<BoxView>> viewRefs,
		List<WeakReference<IElementHandler>> handlerRefs,
		List<WeakReference<IMauiContext>> contextRefs,
		List<WeakReference<PayloadServiceProvider>> providerRefs,
		List<PayloadWeakReference> payloadRefs,
		int index)
	{
		var payload = new PayloadService(index, PayloadBytes);
		var provider = new PayloadServiceProvider(hostContext.Services, payload);
		var androidContext = hostContext.Context ?? throw new InvalidOperationException("Android context is not available.");
		var mauiContext = new MauiContext(provider, androidContext);
		var view = new BoxView
		{
			WidthRequest = 48,
			HeightRequest = 48,
			Color = Colors.DeepSkyBlue,
			BindingContext = payload
		};

		var container = new CoreContainerView(mauiContext)
		{
			CurrentView = view
		};
		var handler = view.Handler ?? throw new InvalidOperationException("ContainerView did not create a handler for the hosted view.");
		var nativePeer = NativePeerRoot.Create(container);

		payloadRefs.Add(new PayloadWeakReference(new WeakReference<PayloadService>(payload), new WeakReference<byte[]>(payload.Bytes)));
		providerRefs.Add(new WeakReference<PayloadServiceProvider>(provider));
		contextRefs.Add(new WeakReference<IMauiContext>(mauiContext));
		viewRefs.Add(new WeakReference<BoxView>(view));
		handlerRefs.Add(new WeakReference<IElementHandler>(handler));
		containerRefs.Add(new WeakReference<CoreContainerView>(container));
		retainedNativeContainers.Add(nativePeer);

		view.BindingContext = null;
		container.CurrentView = null;
		handler.DisconnectHandler();
		view.Handler = null;

		if (clearContainerContext)
			ContainerContextField.SetValue(container, null);
	}

	static void ForceFullGc()
	{
		for (var i = 0; i < 4; i++)
		{
			GC.Collect();
			GC.WaitForPendingFinalizers();
			GC.Collect();
			Java.Lang.JavaSystem.Gc();
			Thread.Sleep(75);
		}
	}

	sealed record NativePeerRoot(IntPtr GlobalRef)
	{
		public static NativePeerRoot Create(CoreContainerView container)
		{
			if (container.Handle == IntPtr.Zero)
				throw new InvalidOperationException("Native ContainerView handle was not available before cleanup.");

			var globalRef = JNIEnv.NewGlobalRef(container.Handle);
			if (globalRef == IntPtr.Zero)
				throw new InvalidOperationException("Failed to create a JNI global reference for the native ContainerView.");

			return new NativePeerRoot(globalRef);
		}
	}

	sealed record PayloadWeakReference(WeakReference<PayloadService> PayloadService, WeakReference<byte[]> Bytes);

	sealed class PayloadService
	{
		public PayloadService(int id, int byteCount)
		{
			Id = id;
			Bytes = new byte[byteCount];
			for (var i = 0; i < Bytes.Length; i += 4096)
				Bytes[i] = (byte)((id + i) % 251);
			Bytes[^1] = (byte)((id + Bytes.Length) % 251);
		}

		public int Id { get; }

		public byte[] Bytes { get; }
	}

	sealed class PayloadServiceProvider : IServiceProvider
	{
		readonly IServiceProvider _fallback;
		readonly PayloadService _payload;

		public PayloadServiceProvider(IServiceProvider fallback, PayloadService payload)
		{
			_fallback = fallback;
			_payload = payload;
		}

		public object? GetService(Type serviceType)
		{
			if (serviceType == typeof(PayloadService))
				return _payload;

			return _fallback.GetService(serviceType);
		}
	}
}

internal sealed record RunStats(
	string Name,
	int Attempts,
	int RetainedNativeGlobalRefs,
	int AliveContainers,
	int AliveViews,
	int AliveHandlers,
	int AliveContexts,
	int AliveProviders,
	int AlivePayloadServices,
	int AlivePayloadByteArrays,
	int ContainersWithContext,
	int ContainersResolvingPayloadService,
	int ContainersWithCurrentView,
	int ContainersWithMainView,
	long RetainedPayloadBytes);

internal sealed record ReproReport(
	int Attempts,
	int PayloadBytes,
	RunStats Control,
	RunStats Current,
	long ManagedHeapBaseline,
	long ManagedHeapFinal)
{
	public bool LeakProved =>
		Control.RetainedNativeGlobalRefs == Attempts &&
		Current.RetainedNativeGlobalRefs == Attempts &&
		Control.AliveContainers == Attempts &&
		Current.AliveContainers == Attempts &&
		Control.AliveViews == 0 &&
		Current.AliveViews == 0 &&
		Control.AliveHandlers == 0 &&
		Current.AliveHandlers == 0 &&
		Control.AliveContexts == 0 &&
		Control.AliveProviders == 0 &&
		Control.AlivePayloadServices == 0 &&
		Control.AlivePayloadByteArrays == 0 &&
		Control.ContainersWithContext == 0 &&
		Control.ContainersResolvingPayloadService == 0 &&
		Control.ContainersWithCurrentView == 0 &&
		Current.ContainersWithCurrentView == 0 &&
		Control.ContainersWithMainView == 0 &&
		Current.ContainersWithMainView == 0 &&
		Current.AliveContexts == Attempts &&
		Current.AliveProviders == Attempts &&
		Current.AlivePayloadServices == Attempts &&
		Current.AlivePayloadByteArrays == Attempts &&
		Current.ContainersWithContext == Attempts &&
		Current.ContainersResolvingPayloadService == Attempts;

	public string ToText()
	{
		return string.Join(Environment.NewLine,
			"AndroidCoreContainerViewMauiContextRetentionRepro",
			$"Retained native core ContainerView peers: {Attempts}",
			$"Payload per attempt: {PayloadBytes / 1024 / 1024} MiB",
			"Source path exercised: Microsoft.Maui.Platform.ContainerView.CurrentView cleanup",
			"Both runs set CurrentView to null, disconnect the hosted BoxView handler, and clear the view BindingContext.",
			"Control-only cleanup clears private ContainerView._context after hosted-view cleanup.",
			$"Leak proved: {LeakProved}",
			string.Empty,
			Format(Control),
			string.Empty,
			Format(Current),
			string.Empty,
			$"Managed heap baseline: {FormatBytes(ManagedHeapBaseline)}",
			$"Managed heap final: {FormatBytes(ManagedHeapFinal)}",
			$"Managed heap delta: {FormatBytes(ManagedHeapFinal - ManagedHeapBaseline)}",
			LeakProved ? "RESULT: PROVEN" : "RESULT: NOT PROVEN");
	}

	string Format(RunStats stats)
	{
		return string.Join(Environment.NewLine,
			$"Run: {stats.Name}",
			$"  retained JNI global refs to native ContainerViews: {stats.RetainedNativeGlobalRefs}/{stats.Attempts}",
			$"  ContainerViews alive after full GC: {stats.AliveContainers}/{stats.Attempts}",
			$"  hosted BoxViews alive after full GC: {stats.AliveViews}/{stats.Attempts}",
			$"  hosted handlers alive after full GC: {stats.AliveHandlers}/{stats.Attempts}",
			$"  MauiContexts alive after full GC: {stats.AliveContexts}/{stats.Attempts}",
			$"  service providers alive after full GC: {stats.AliveProviders}/{stats.Attempts}",
			$"  payload services alive after full GC: {stats.AlivePayloadServices}/{stats.Attempts}",
			$"  payload byte arrays alive after full GC: {stats.AlivePayloadByteArrays}/{stats.Attempts}",
			$"  retained ContainerView._context fields: {stats.ContainersWithContext}/{stats.Attempts}",
			$"  retained containers resolving payload service: {stats.ContainersResolvingPayloadService}/{stats.Attempts}",
			$"  retained ContainerView.CurrentView values: {stats.ContainersWithCurrentView}/{stats.Attempts}",
			$"  retained ContainerView.MainView values: {stats.ContainersWithMainView}/{stats.Attempts}",
			$"  retained context payload bytes: {FormatBytes(stats.RetainedPayloadBytes)} ({stats.RetainedPayloadBytes * 100.0 / (PayloadBytes * stats.Attempts):0.0}%)");
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
