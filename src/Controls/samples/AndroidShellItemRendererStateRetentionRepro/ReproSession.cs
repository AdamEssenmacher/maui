#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Android.Content;
using AndroidX.DrawerLayout.Widget;
using Microsoft.Maui;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Controls.Platform.Compatibility;
using AToolbar = AndroidX.AppCompat.Widget.Toolbar;

namespace AndroidShellItemRendererStateRetentionRepro;

public sealed record RunStats(
	string Name,
	int DisconnectAttempts,
	int DestroyAttempts,
	int AliveDisconnectedRenderers,
	int AliveDestroyedRenderers,
	int AliveShellItems,
	int AliveShells,
	int AliveShellContexts,
	int AliveMauiContexts,
	int AliveProviders,
	int AlivePayloadServices,
	int AlivePayloadByteArrays,
	int DisconnectedRenderersWithShellItem,
	int DisconnectedRenderersWithShellContext,
	int DestroyedRenderersWithShellItem,
	int DestroyedRenderersWithShellContext,
	int DestroyedRenderersResolvingPayloadService,
	long RetainedPayloadBytes);

public sealed record ReproReport(
	int DisconnectAttempts,
	int DestroyAttempts,
	int PayloadBytes,
	RunStats Control,
	RunStats Current,
	long ManagedHeapBaseline,
	long ManagedHeapFinal)
{
	public int TotalAttempts => DisconnectAttempts + DestroyAttempts;

	public bool LeakProved =>
		Control.AliveShellItems == 0 &&
		Control.AliveShells == 0 &&
		Control.AliveShellContexts == 0 &&
		Control.AliveMauiContexts == 0 &&
		Control.AliveProviders == 0 &&
		Control.AlivePayloadServices == 0 &&
		Control.AlivePayloadByteArrays == 0 &&
		Control.DisconnectedRenderersWithShellItem == 0 &&
		Control.DisconnectedRenderersWithShellContext == 0 &&
		Control.DestroyedRenderersWithShellItem == 0 &&
		Control.DestroyedRenderersWithShellContext == 0 &&
		Current.AliveDisconnectedRenderers == DisconnectAttempts &&
		Current.AliveDestroyedRenderers == DestroyAttempts &&
		Current.AliveShellItems == TotalAttempts &&
		Current.AliveShells == TotalAttempts &&
		Current.AliveShellContexts == DestroyAttempts &&
		Current.AliveMauiContexts == DestroyAttempts &&
		Current.AliveProviders == DestroyAttempts &&
		Current.AlivePayloadServices == TotalAttempts &&
		Current.AlivePayloadByteArrays == TotalAttempts &&
		Current.DisconnectedRenderersWithShellItem == DisconnectAttempts &&
		Current.DisconnectedRenderersWithShellContext == 0 &&
		Current.DestroyedRenderersWithShellItem == DestroyAttempts &&
		Current.DestroyedRenderersWithShellContext == DestroyAttempts &&
		Current.DestroyedRenderersResolvingPayloadService == DestroyAttempts;

	public string ToText()
	{
		return string.Join(Environment.NewLine,
			"AndroidShellItemRendererStateRetentionRepro",
			$"Disconnect attempts: {DisconnectAttempts}",
			$"Destroy attempts: {DestroyAttempts}",
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
			$"  retained disconnected ShellItemRenderers: {stats.DisconnectAttempts}",
			$"  retained destroyed ShellItemRenderers: {stats.DestroyAttempts}",
			$"  disconnected renderers alive after full GC: {stats.AliveDisconnectedRenderers}/{stats.DisconnectAttempts}",
			$"  destroyed renderers alive after full GC: {stats.AliveDestroyedRenderers}/{stats.DestroyAttempts}",
			$"  ShellItems alive after full GC: {stats.AliveShellItems}/{stats.DisconnectAttempts + stats.DestroyAttempts}",
			$"  Shells alive after full GC: {stats.AliveShells}/{stats.DisconnectAttempts + stats.DestroyAttempts}",
			$"  ShellContexts alive after full GC: {stats.AliveShellContexts}/{stats.DestroyAttempts}",
			$"  MauiContexts alive after full GC: {stats.AliveMauiContexts}/{stats.DestroyAttempts}",
			$"  service providers alive after full GC: {stats.AliveProviders}/{stats.DestroyAttempts}",
			$"  payload services alive after full GC: {stats.AlivePayloadServices}/{stats.DisconnectAttempts + stats.DestroyAttempts}",
			$"  payload byte arrays alive after full GC: {stats.AlivePayloadByteArrays}/{stats.DisconnectAttempts + stats.DestroyAttempts}",
			$"  disconnected renderer ShellItem fields: {stats.DisconnectedRenderersWithShellItem}/{stats.DisconnectAttempts}",
			$"  disconnected renderer ShellContext fields: {stats.DisconnectedRenderersWithShellContext}/{stats.DisconnectAttempts}",
			$"  destroyed renderer ShellItem fields: {stats.DestroyedRenderersWithShellItem}/{stats.DestroyAttempts}",
			$"  destroyed renderer ShellContext fields: {stats.DestroyedRenderersWithShellContext}/{stats.DestroyAttempts}",
			$"  destroyed renderers resolving payload service: {stats.DestroyedRenderersResolvingPayloadService}/{stats.DestroyAttempts}",
			$"  retained payload bytes: {FormatBytes(stats.RetainedPayloadBytes)} ({stats.RetainedPayloadBytes * 100.0 / (PayloadBytes * (stats.DisconnectAttempts + stats.DestroyAttempts)):0.0}%)");
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
	const int DisconnectAttempts = 48;
	const int DestroyAttempts = 48;
	const int PayloadBytes = 1024 * 1024;

	static readonly Type ShellItemRendererBaseType = typeof(ShellItemRenderer).BaseType
		?? throw new MissingMemberException("ShellItemRendererBase");

	static readonly MethodInfo DisconnectMethod =
		ShellItemRendererBaseType.GetMethod("Disconnect", BindingFlags.Instance | BindingFlags.NonPublic)
		?? throw new MissingMemberException(ShellItemRendererBaseType.FullName, "Disconnect");

	static readonly FieldInfo ShellItemField =
		ShellItemRendererBaseType.GetField("<ShellItem>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)
		?? throw new MissingMemberException(ShellItemRendererBaseType.FullName, "<ShellItem>k__BackingField");

	static readonly FieldInfo ShellContextField =
		ShellItemRendererBaseType.GetField("<ShellContext>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)
		?? throw new MissingMemberException(ShellItemRendererBaseType.FullName, "<ShellContext>k__BackingField");

	public static async Task<ReproReport> RunAsync(IMauiContext hostContext)
	{
		await Task.Yield();

		ForceFullGc();
		var baseline = GC.GetTotalMemory(forceFullCollection: true);

		var control = await RunScenarioAsync(
			hostContext,
			"control: clear ShellItemRendererBase ShellItem/ShellContext fields",
			clearRetainedState: true);

		var current = await RunScenarioAsync(
			hostContext,
			"current: run ShellItemRenderer cleanup only",
			clearRetainedState: false);

		ForceFullGc();
		var final = GC.GetTotalMemory(forceFullCollection: true);

		return new ReproReport(DisconnectAttempts, DestroyAttempts, PayloadBytes, control, current, baseline, final);
	}

	static async Task<RunStats> RunScenarioAsync(IMauiContext hostContext, string name, bool clearRetainedState)
	{
		var retainedDisconnectedRenderers = new List<ShellItemRenderer>(DisconnectAttempts);
		var retainedDestroyedRenderers = new List<ShellItemRenderer>(DestroyAttempts);
		var disconnectedRendererRefs = new List<WeakReference<ShellItemRenderer>>(DisconnectAttempts);
		var destroyedRendererRefs = new List<WeakReference<ShellItemRenderer>>(DestroyAttempts);
		var shellItemRefs = new List<WeakReference<ShellItem>>(DisconnectAttempts + DestroyAttempts);
		var shellRefs = new List<WeakReference<Shell>>(DisconnectAttempts + DestroyAttempts);
		var shellContextRefs = new List<WeakReference<PayloadShellContext>>(DisconnectAttempts + DestroyAttempts);
		var mauiContextRefs = new List<WeakReference<IMauiContext>>(DisconnectAttempts + DestroyAttempts);
		var providerRefs = new List<WeakReference<PayloadServiceProvider>>(DisconnectAttempts + DestroyAttempts);
		var payloadRefs = new List<PayloadWeakReference>(DisconnectAttempts + DestroyAttempts);

		for (var i = 0; i < DisconnectAttempts; i++)
		{
			CreateDisconnectedRenderer(
				hostContext,
				clearRetainedState,
				retainedDisconnectedRenderers,
				disconnectedRendererRefs,
				shellItemRefs,
				shellRefs,
				shellContextRefs,
				mauiContextRefs,
				providerRefs,
				payloadRefs,
				i);

			if (i % 12 == 0)
				await Task.Yield();
		}

		for (var i = 0; i < DestroyAttempts; i++)
		{
			CreateDestroyedRenderer(
				hostContext,
				clearRetainedState,
				retainedDestroyedRenderers,
				destroyedRendererRefs,
				shellItemRefs,
				shellRefs,
				shellContextRefs,
				mauiContextRefs,
				providerRefs,
				payloadRefs,
				i + DisconnectAttempts);

			if (i % 12 == 0)
				await Task.Yield();
		}

		ForceFullGc();
		GC.KeepAlive(retainedDisconnectedRenderers);
		GC.KeepAlive(retainedDestroyedRenderers);

		var aliveDisconnectedRenderers = disconnectedRendererRefs.Count(static wr => wr.TryGetTarget(out _));
		var aliveDestroyedRenderers = destroyedRendererRefs.Count(static wr => wr.TryGetTarget(out _));
		var aliveShellItems = shellItemRefs.Count(static wr => wr.TryGetTarget(out _));
		var aliveShells = shellRefs.Count(static wr => wr.TryGetTarget(out _));
		var aliveShellContexts = shellContextRefs.Count(static wr => wr.TryGetTarget(out _));
		var aliveMauiContexts = mauiContextRefs.Count(static wr => wr.TryGetTarget(out _));
		var aliveProviders = providerRefs.Count(static wr => wr.TryGetTarget(out _));
		var alivePayloadServices = payloadRefs.Count(static wr => wr.PayloadService.TryGetTarget(out _));
		var alivePayloadByteArrays = payloadRefs.Count(static wr => wr.Bytes.TryGetTarget(out _));
		var disconnectedRenderersWithShellItem = disconnectedRendererRefs.Count(RendererHasShellItem);
		var disconnectedRenderersWithShellContext = disconnectedRendererRefs.Count(RendererHasShellContext);
		var destroyedRenderersWithShellItem = destroyedRendererRefs.Count(RendererHasShellItem);
		var destroyedRenderersWithShellContext = destroyedRendererRefs.Count(RendererHasShellContext);
		var destroyedRenderersResolvingPayloadService = destroyedRendererRefs.Count(static wr =>
			wr.TryGetTarget(out var renderer) &&
			ShellContextField.GetValue(renderer) is PayloadShellContext context &&
			context.MauiContext.Services.GetService(typeof(PayloadService)) is PayloadService);

		return new RunStats(
			name,
			DisconnectAttempts,
			DestroyAttempts,
			aliveDisconnectedRenderers,
			aliveDestroyedRenderers,
			aliveShellItems,
			aliveShells,
			aliveShellContexts,
			aliveMauiContexts,
			aliveProviders,
			alivePayloadServices,
			alivePayloadByteArrays,
			disconnectedRenderersWithShellItem,
			disconnectedRenderersWithShellContext,
			destroyedRenderersWithShellItem,
			destroyedRenderersWithShellContext,
			destroyedRenderersResolvingPayloadService,
			(long)alivePayloadByteArrays * PayloadBytes);
	}

	static void CreateDisconnectedRenderer(
		IMauiContext hostContext,
		bool clearRetainedState,
		List<ShellItemRenderer> retainedRenderers,
		List<WeakReference<ShellItemRenderer>> rendererRefs,
		List<WeakReference<ShellItem>> shellItemRefs,
		List<WeakReference<Shell>> shellRefs,
		List<WeakReference<PayloadShellContext>> shellContextRefs,
		List<WeakReference<IMauiContext>> mauiContextRefs,
		List<WeakReference<PayloadServiceProvider>> providerRefs,
		List<PayloadWeakReference> payloadRefs,
		int index)
	{
		CreateRendererGraph(hostContext, index, out var renderer, out var shell, out var shellItem, out var shellContext, out var mauiContext, out var provider, out var payload);

		TrackGraph(renderer, shell, shellItem, shellContext, mauiContext, provider, payload, rendererRefs, shellItemRefs, shellRefs, shellContextRefs, mauiContextRefs, providerRefs, payloadRefs);
		retainedRenderers.Add(renderer);

		DisconnectMethod.Invoke(renderer, Array.Empty<object>());

		if (clearRetainedState)
		{
			ShellItemField.SetValue(renderer, null);
			ShellContextField.SetValue(renderer, null);
		}
	}

	static void CreateDestroyedRenderer(
		IMauiContext hostContext,
		bool clearRetainedState,
		List<ShellItemRenderer> retainedRenderers,
		List<WeakReference<ShellItemRenderer>> rendererRefs,
		List<WeakReference<ShellItem>> shellItemRefs,
		List<WeakReference<Shell>> shellRefs,
		List<WeakReference<PayloadShellContext>> shellContextRefs,
		List<WeakReference<IMauiContext>> mauiContextRefs,
		List<WeakReference<PayloadServiceProvider>> providerRefs,
		List<PayloadWeakReference> payloadRefs,
		int index)
	{
		CreateRendererGraph(hostContext, index, out var renderer, out var shell, out var shellItem, out var shellContext, out var mauiContext, out var provider, out var payload);

		TrackGraph(renderer, shell, shellItem, shellContext, mauiContext, provider, payload, rendererRefs, shellItemRefs, shellRefs, shellContextRefs, mauiContextRefs, providerRefs, payloadRefs);
		retainedRenderers.Add(renderer);

		renderer.OnDestroy();

		if (clearRetainedState)
		{
			ShellItemField.SetValue(renderer, null);
			ShellContextField.SetValue(renderer, null);
		}
	}

	static void CreateRendererGraph(
		IMauiContext hostContext,
		int index,
		out ShellItemRenderer renderer,
		out Shell shell,
		out ShellItem shellItem,
		out PayloadShellContext shellContext,
		out IMauiContext mauiContext,
		out PayloadServiceProvider provider,
		out PayloadService payload)
	{
		payload = new PayloadService(index, PayloadBytes);
		provider = new PayloadServiceProvider(hostContext.Services, payload);
		var androidContext = hostContext.Context ?? throw new InvalidOperationException("Android context is not available.");
		mauiContext = new MauiContext(provider, androidContext);
		shell = new Shell { Title = $"Retired shell {index:0000}" };
		shellItem = new FlyoutItem
		{
			Title = $"Flyout item {index:0000}",
			BindingContext = payload
		};
		shell.Items.Add(shellItem);
		shellContext = new PayloadShellContext(androidContext, shell, mauiContext);
		renderer = new ShellItemRenderer(shellContext);
		((IShellItemRenderer)renderer).ShellItem = shellItem;
	}

	static void TrackGraph(
		ShellItemRenderer renderer,
		Shell shell,
		ShellItem shellItem,
		PayloadShellContext shellContext,
		IMauiContext mauiContext,
		PayloadServiceProvider provider,
		PayloadService payload,
		List<WeakReference<ShellItemRenderer>> rendererRefs,
		List<WeakReference<ShellItem>> shellItemRefs,
		List<WeakReference<Shell>> shellRefs,
		List<WeakReference<PayloadShellContext>> shellContextRefs,
		List<WeakReference<IMauiContext>> mauiContextRefs,
		List<WeakReference<PayloadServiceProvider>> providerRefs,
		List<PayloadWeakReference> payloadRefs)
	{
		rendererRefs.Add(new WeakReference<ShellItemRenderer>(renderer));
		shellItemRefs.Add(new WeakReference<ShellItem>(shellItem));
		shellRefs.Add(new WeakReference<Shell>(shell));
		shellContextRefs.Add(new WeakReference<PayloadShellContext>(shellContext));
		mauiContextRefs.Add(new WeakReference<IMauiContext>(mauiContext));
		providerRefs.Add(new WeakReference<PayloadServiceProvider>(provider));
		payloadRefs.Add(new PayloadWeakReference(new WeakReference<PayloadService>(payload), new WeakReference<byte[]>(payload.Bytes)));
	}

	static bool RendererHasShellItem(WeakReference<ShellItemRenderer> wr)
	{
		return wr.TryGetTarget(out var renderer) &&
			ShellItemField.GetValue(renderer) is ShellItem;
	}

	static bool RendererHasShellContext(WeakReference<ShellItemRenderer> wr)
	{
		return wr.TryGetTarget(out var renderer) &&
			ShellContextField.GetValue(renderer) is IShellContext;
	}

	static void ForceFullGc()
	{
		for (var i = 0; i < 4; i++)
		{
			GC.Collect();
			GC.WaitForPendingFinalizers();
			GC.Collect();
			Java.Lang.JavaSystem.Gc();
			Thread.Sleep(50);
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

	sealed class PayloadShellContext : IShellContext
	{
		public PayloadShellContext(Context androidContext, Shell shell, IMauiContext mauiContext)
		{
			AndroidContext = androidContext;
			Shell = shell;
			MauiContext = mauiContext;
		}

		public Context AndroidContext { get; }

		public Shell Shell { get; }

		public IMauiContext MauiContext { get; }

		public DrawerLayout CurrentDrawerLayout => throw new NotSupportedException();

		public IShellObservableFragment CreateFragmentForPage(Page page) => throw new NotSupportedException();

		public IShellFlyoutContentRenderer CreateShellFlyoutContentRenderer() => throw new NotSupportedException();

		public IShellItemRenderer CreateShellItemRenderer(ShellItem shellItem) => throw new NotSupportedException();

		public IShellSectionRenderer CreateShellSectionRenderer(ShellSection shellSection) => throw new NotSupportedException();

		public IShellToolbarTracker CreateTrackerForToolbar(AToolbar toolbar) => throw new NotSupportedException();

		public IShellToolbarAppearanceTracker CreateToolbarAppearanceTracker() => throw new NotSupportedException();

		public IShellTabLayoutAppearanceTracker CreateTabLayoutAppearanceTracker(ShellSection shellSection) => throw new NotSupportedException();

		public IShellBottomNavViewAppearanceTracker CreateBottomNavViewAppearanceTracker(ShellItem shellItem) => throw new NotSupportedException();
	}
}
