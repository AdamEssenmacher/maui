#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.Maui;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Graphics;
using ShellContainerView = Microsoft.Maui.Controls.Platform.Compatibility.ContainerView;

namespace AndroidShellContainerViewMauiContextRetentionRepro;

public sealed record RunStats(
	string Name,
	int Attempts,
	int AliveContainers,
	int AliveViews,
	int AliveHandlers,
	int AliveContexts,
	int AliveProviders,
	int AlivePayloadServices,
	int AlivePayloadByteArrays,
	int ContainersWithMauiContext,
	int NestedRenderersWithMauiContext,
	int ContainersResolvingPayloadService,
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
		Control.AliveContexts == 0 &&
		Control.AliveProviders == 0 &&
		Control.AlivePayloadServices == 0 &&
		Control.AlivePayloadByteArrays == 0 &&
		Control.ContainersWithMauiContext == 0 &&
		Control.NestedRenderersWithMauiContext == 0 &&
		Control.ContainersResolvingPayloadService == 0 &&
		Current.AliveViews == 0 &&
		Current.AliveHandlers == 0 &&
		Current.AliveContexts == Attempts &&
		Current.AliveProviders == Attempts &&
		Current.AlivePayloadServices == Attempts &&
		Current.AlivePayloadByteArrays == Attempts &&
		Current.ContainersWithMauiContext == Attempts &&
		Current.NestedRenderersWithMauiContext == Attempts &&
		Current.ContainersResolvingPayloadService == Attempts;

	public string ToText()
	{
		return string.Join(Environment.NewLine,
			"AndroidShellContainerViewMauiContextRetentionRepro",
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
			$"Managed heap delta: {FormatBytes(ManagedHeapFinal - ManagedHeapBaseline)}",
			LeakProved ? "RESULT: PROVEN" : "RESULT: NOT PROVEN");
	}

	string Format(RunStats stats)
	{
		return string.Join(Environment.NewLine,
			$"Run: {stats.Name}",
			$"  retained disposed native ContainerViews: {stats.Attempts}",
			$"  ContainerViews alive after full GC: {stats.AliveContainers}/{stats.Attempts}",
			$"  hosted Views alive after full GC: {stats.AliveViews}/{stats.Attempts}",
			$"  hosted handlers alive after full GC: {stats.AliveHandlers}/{stats.Attempts}",
			$"  MauiContexts alive after full GC: {stats.AliveContexts}/{stats.Attempts}",
			$"  service providers alive after full GC: {stats.AliveProviders}/{stats.Attempts}",
			$"  payload services alive after full GC: {stats.AlivePayloadServices}/{stats.Attempts}",
			$"  payload byte arrays alive after full GC: {stats.AlivePayloadByteArrays}/{stats.Attempts}",
			$"  retained ContainerView._mauiContext fields: {stats.ContainersWithMauiContext}/{stats.Attempts}",
			$"  retained ShellViewRenderer._mauiContext fields: {stats.NestedRenderersWithMauiContext}/{stats.Attempts}",
			$"  retained containers resolving payload service: {stats.ContainersResolvingPayloadService}/{stats.Attempts}",
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

internal static class ReproSession
{
	const int Attempts = 96;
	const int PayloadBytes = 1024 * 1024;

	static readonly FieldInfo ContainerMauiContextField =
		typeof(ShellContainerView).GetField("_mauiContext", BindingFlags.Instance | BindingFlags.NonPublic)
		?? throw new MissingMemberException(nameof(ShellContainerView), "_mauiContext");

	static readonly FieldInfo ContainerShellContentViewField =
		typeof(ShellContainerView).GetField("_shellContentView", BindingFlags.Instance | BindingFlags.NonPublic)
		?? throw new MissingMemberException(nameof(ShellContainerView), "_shellContentView");

	static readonly FieldInfo ShellViewRendererMauiContextField =
		ContainerShellContentViewField.FieldType.GetField("_mauiContext", BindingFlags.Instance | BindingFlags.NonPublic)
		?? throw new MissingMemberException(ContainerShellContentViewField.FieldType.FullName, "_mauiContext");

	public static async Task<ReproReport> RunAsync(IMauiContext hostContext)
	{
		await Task.Yield();

		ForceFullGc();
		var baseline = GC.GetTotalMemory(forceFullCollection: true);

		var control = await RunScenarioAsync(
			hostContext,
			"control: clear ContainerView and ShellViewRenderer MauiContexts",
			clearMauiContext: true);

		var current = await RunScenarioAsync(
			hostContext,
			"current: dispose ContainerView only",
			clearMauiContext: false);

		ForceFullGc();
		var final = GC.GetTotalMemory(forceFullCollection: true);

		return new ReproReport(Attempts, PayloadBytes, control, current, baseline, final);
	}

	static async Task<RunStats> RunScenarioAsync(IMauiContext hostContext, string name, bool clearMauiContext)
	{
		var retainedNativeContainers = new List<ShellContainerView>(Attempts);
		var containerRefs = new List<WeakReference<ShellContainerView>>(Attempts);
		var viewRefs = new List<WeakReference<BoxView>>(Attempts);
		var handlerRefs = new List<WeakReference<IViewHandler>>(Attempts);
		var contextRefs = new List<WeakReference<IMauiContext>>(Attempts);
		var providerRefs = new List<WeakReference<PayloadServiceProvider>>(Attempts);
		var payloadRefs = new List<PayloadWeakReference>(Attempts);

		for (var i = 0; i < Attempts; i++)
		{
			CreateDisposedContainer(
				hostContext,
				clearMauiContext,
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

		ForceFullGc();
		GC.KeepAlive(retainedNativeContainers);

		var aliveContainers = containerRefs.Count(static wr => wr.TryGetTarget(out _));
		var aliveViews = viewRefs.Count(static wr => wr.TryGetTarget(out _));
		var aliveHandlers = handlerRefs.Count(static wr => wr.TryGetTarget(out _));
		var aliveContexts = contextRefs.Count(static wr => wr.TryGetTarget(out _));
		var aliveProviders = providerRefs.Count(static wr => wr.TryGetTarget(out _));
		var alivePayloadServices = payloadRefs.Count(static wr => wr.PayloadService.TryGetTarget(out _));
		var alivePayloadByteArrays = payloadRefs.Count(static wr => wr.Bytes.TryGetTarget(out _));
		var containersWithMauiContext = containerRefs.Count(static wr =>
			wr.TryGetTarget(out var container) &&
			ContainerMauiContextField.GetValue(container) is IMauiContext);
		var nestedRenderersWithMauiContext = containerRefs.Count(static wr =>
			wr.TryGetTarget(out var container) &&
			TryGetNestedMauiContext(container, out _));
		var containersResolvingPayloadService = containerRefs.Count(static wr =>
			wr.TryGetTarget(out var container) &&
			ContainerMauiContextField.GetValue(container) is IMauiContext context &&
			context.Services.GetService(typeof(PayloadService)) is PayloadService);

		return new RunStats(
			name,
			Attempts,
			aliveContainers,
			aliveViews,
			aliveHandlers,
			aliveContexts,
			aliveProviders,
			alivePayloadServices,
			alivePayloadByteArrays,
			containersWithMauiContext,
			nestedRenderersWithMauiContext,
			containersResolvingPayloadService,
			(long)alivePayloadByteArrays * PayloadBytes);
	}

	static void CreateDisposedContainer(
		IMauiContext hostContext,
		bool clearMauiContext,
		List<ShellContainerView> retainedNativeContainers,
		List<WeakReference<ShellContainerView>> containerRefs,
		List<WeakReference<BoxView>> viewRefs,
		List<WeakReference<IViewHandler>> handlerRefs,
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
			Color = Colors.CornflowerBlue
		};

		var container = new ShellContainerView(androidContext, view, mauiContext);
		var handler = view.Handler ?? throw new InvalidOperationException("ContainerView did not create a handler for the hosted view.");

		payloadRefs.Add(new PayloadWeakReference(new WeakReference<PayloadService>(payload), new WeakReference<byte[]>(payload.Bytes)));
		providerRefs.Add(new WeakReference<PayloadServiceProvider>(provider));
		contextRefs.Add(new WeakReference<IMauiContext>(mauiContext));
		viewRefs.Add(new WeakReference<BoxView>(view));
		handlerRefs.Add(new WeakReference<IViewHandler>(handler));
		containerRefs.Add(new WeakReference<ShellContainerView>(container));
		retainedNativeContainers.Add(container);

		container.Dispose();

		if (clearMauiContext)
			ClearContextFields(container);
	}

	static void ClearContextFields(ShellContainerView container)
	{
		var shellViewRenderer = ContainerShellContentViewField.GetValue(container);
		if (shellViewRenderer is not null)
			ShellViewRendererMauiContextField.SetValue(shellViewRenderer, null);

		ContainerMauiContextField.SetValue(container, null);
	}

	static bool TryGetNestedMauiContext(ShellContainerView container, out IMauiContext? mauiContext)
	{
		mauiContext = null;
		var shellViewRenderer = ContainerShellContentViewField.GetValue(container);
		if (shellViewRenderer is null)
			return false;

		mauiContext = ShellViewRendererMauiContextField.GetValue(shellViewRenderer) as IMauiContext;
		return mauiContext is not null;
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
