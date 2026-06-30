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

namespace AndroidShellSectionRendererStateRetentionRepro;

public sealed record RunStats(
	string Name,
	int Attempts,
	int AliveRenderers,
	int AliveShellSections,
	int AliveShells,
	int AliveShellContexts,
	int AliveMauiContexts,
	int AliveProviders,
	int AlivePayloadServices,
	int AlivePayloadByteArrays,
	int RenderersWithShellSection,
	int RenderersWithShellContext,
	int RenderersResolvingPayloadService,
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
		Control.AliveShellSections == 0 &&
		Control.AliveShells == 0 &&
		Control.AliveShellContexts == 0 &&
		Control.AliveMauiContexts == 0 &&
		Control.AliveProviders == 0 &&
		Control.AlivePayloadServices == 0 &&
		Control.AlivePayloadByteArrays == 0 &&
		Control.RenderersWithShellSection == 0 &&
		Control.RenderersWithShellContext == 0 &&
		Control.RenderersResolvingPayloadService == 0 &&
		Current.AliveRenderers == Attempts &&
		Current.AliveShellSections == Attempts &&
		Current.AliveShells == Attempts &&
		Current.AliveShellContexts == Attempts &&
		Current.AliveMauiContexts == Attempts &&
		Current.AliveProviders == Attempts &&
		Current.AlivePayloadServices == Attempts &&
		Current.AlivePayloadByteArrays == Attempts &&
		Current.RenderersWithShellSection == Attempts &&
		Current.RenderersWithShellContext == Attempts &&
		Current.RenderersResolvingPayloadService == Attempts;

	public string ToText()
	{
		return string.Join(Environment.NewLine,
			"AndroidShellSectionRendererStateRetentionRepro",
			$"Renderer attempts: {Attempts}",
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
			$"  retained disposed ShellSectionRenderers: {stats.Attempts}",
			$"  renderers alive after full GC: {stats.AliveRenderers}/{stats.Attempts}",
			$"  ShellSections alive after full GC: {stats.AliveShellSections}/{stats.Attempts}",
			$"  Shells alive after full GC: {stats.AliveShells}/{stats.Attempts}",
			$"  ShellContexts alive after full GC: {stats.AliveShellContexts}/{stats.Attempts}",
			$"  MauiContexts alive after full GC: {stats.AliveMauiContexts}/{stats.Attempts}",
			$"  service providers alive after full GC: {stats.AliveProviders}/{stats.Attempts}",
			$"  payload services alive after full GC: {stats.AlivePayloadServices}/{stats.Attempts}",
			$"  payload byte arrays alive after full GC: {stats.AlivePayloadByteArrays}/{stats.Attempts}",
			$"  retained renderer ShellSection properties: {stats.RenderersWithShellSection}/{stats.Attempts}",
			$"  retained renderer _shellContext fields: {stats.RenderersWithShellContext}/{stats.Attempts}",
			$"  renderers resolving payload service: {stats.RenderersResolvingPayloadService}/{stats.Attempts}",
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

internal static class ReproSession
{
	const int Attempts = 96;
	const int PayloadBytes = 1024 * 1024;

	static readonly FieldInfo ShellContextField =
		typeof(ShellSectionRenderer).GetField("_shellContext", BindingFlags.Instance | BindingFlags.NonPublic)
		?? throw new MissingMemberException(typeof(ShellSectionRenderer).FullName, "_shellContext");

	public static async Task<ReproReport> RunAsync(IMauiContext hostContext)
	{
		await Task.Yield();

		ForceFullGc();
		var baseline = GC.GetTotalMemory(forceFullCollection: true);

		var control = await RunScenarioAsync(
			hostContext,
			"control: clear ShellSectionRenderer ShellSection/_shellContext after dispose",
			clearRetainedState: true);

		var current = await RunScenarioAsync(
			hostContext,
			"current: dispose ShellSectionRenderer only",
			clearRetainedState: false);

		ForceFullGc();
		var final = GC.GetTotalMemory(forceFullCollection: true);

		return new ReproReport(Attempts, PayloadBytes, control, current, baseline, final);
	}

	static async Task<RunStats> RunScenarioAsync(IMauiContext hostContext, string name, bool clearRetainedState)
	{
		var retainedRenderers = new List<ShellSectionRenderer>(Attempts);
		var rendererRefs = new List<WeakReference<ShellSectionRenderer>>(Attempts);
		var shellSectionRefs = new List<WeakReference<ShellSection>>(Attempts);
		var shellRefs = new List<WeakReference<Shell>>(Attempts);
		var shellContextRefs = new List<WeakReference<PayloadShellContext>>(Attempts);
		var mauiContextRefs = new List<WeakReference<IMauiContext>>(Attempts);
		var providerRefs = new List<WeakReference<PayloadServiceProvider>>(Attempts);
		var payloadRefs = new List<PayloadWeakReference>(Attempts);

		for (var i = 0; i < Attempts; i++)
		{
			CreateDisposedRenderer(
				hostContext,
				clearRetainedState,
				retainedRenderers,
				rendererRefs,
				shellSectionRefs,
				shellRefs,
				shellContextRefs,
				mauiContextRefs,
				providerRefs,
				payloadRefs,
				i);

			if (i % 12 == 0)
				await Task.Yield();
		}

		ForceFullGc();
		GC.KeepAlive(retainedRenderers);

		var aliveRenderers = rendererRefs.Count(static wr => wr.TryGetTarget(out _));
		var aliveShellSections = shellSectionRefs.Count(static wr => wr.TryGetTarget(out _));
		var aliveShells = shellRefs.Count(static wr => wr.TryGetTarget(out _));
		var aliveShellContexts = shellContextRefs.Count(static wr => wr.TryGetTarget(out _));
		var aliveMauiContexts = mauiContextRefs.Count(static wr => wr.TryGetTarget(out _));
		var aliveProviders = providerRefs.Count(static wr => wr.TryGetTarget(out _));
		var alivePayloadServices = payloadRefs.Count(static wr => wr.PayloadService.TryGetTarget(out _));
		var alivePayloadByteArrays = payloadRefs.Count(static wr => wr.Bytes.TryGetTarget(out _));
		var renderersWithShellSection = rendererRefs.Count(static wr =>
			wr.TryGetTarget(out var renderer) &&
			renderer.ShellSection is not null);
		var renderersWithShellContext = rendererRefs.Count(static wr =>
			wr.TryGetTarget(out var renderer) &&
			ShellContextField.GetValue(renderer) is IShellContext);
		var renderersResolvingPayloadService = rendererRefs.Count(static wr =>
			wr.TryGetTarget(out var renderer) &&
			ShellContextField.GetValue(renderer) is PayloadShellContext context &&
			context.MauiContext.Services.GetService(typeof(PayloadService)) is PayloadService);

		return new RunStats(
			name,
			Attempts,
			aliveRenderers,
			aliveShellSections,
			aliveShells,
			aliveShellContexts,
			aliveMauiContexts,
			aliveProviders,
			alivePayloadServices,
			alivePayloadByteArrays,
			renderersWithShellSection,
			renderersWithShellContext,
			renderersResolvingPayloadService,
			(long)alivePayloadByteArrays * PayloadBytes);
	}

	static void CreateDisposedRenderer(
		IMauiContext hostContext,
		bool clearRetainedState,
		List<ShellSectionRenderer> retainedRenderers,
		List<WeakReference<ShellSectionRenderer>> rendererRefs,
		List<WeakReference<ShellSection>> shellSectionRefs,
		List<WeakReference<Shell>> shellRefs,
		List<WeakReference<PayloadShellContext>> shellContextRefs,
		List<WeakReference<IMauiContext>> mauiContextRefs,
		List<WeakReference<PayloadServiceProvider>> providerRefs,
		List<PayloadWeakReference> payloadRefs,
		int index)
	{
		CreateRendererGraph(hostContext, index, out var renderer, out var shell, out var section, out var shellContext, out var mauiContext, out var provider, out var payload);

		rendererRefs.Add(new WeakReference<ShellSectionRenderer>(renderer));
		shellSectionRefs.Add(new WeakReference<ShellSection>(section));
		shellRefs.Add(new WeakReference<Shell>(shell));
		shellContextRefs.Add(new WeakReference<PayloadShellContext>(shellContext));
		mauiContextRefs.Add(new WeakReference<IMauiContext>(mauiContext));
		providerRefs.Add(new WeakReference<PayloadServiceProvider>(provider));
		payloadRefs.Add(new PayloadWeakReference(new WeakReference<PayloadService>(payload), new WeakReference<byte[]>(payload.Bytes)));
		retainedRenderers.Add(renderer);

		renderer.Dispose();

		if (clearRetainedState)
		{
			renderer.ShellSection = null;
			ShellContextField.SetValue(renderer, null);
		}
	}

	static void CreateRendererGraph(
		IMauiContext hostContext,
		int index,
		out ShellSectionRenderer renderer,
		out Shell shell,
		out ShellSection section,
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
		section = new ShellSection
		{
			Title = $"Retired section {index:0000}",
			BindingContext = payload
		};
		section.Items.Add(new ShellContent
		{
			Title = $"Content {index:0000}",
			Content = new ContentPage { Title = $"Root {index:0000}" }
		});
		shell.Items.Add(new TabBar
		{
			Title = $"TabBar {index:0000}",
			Items = { section }
		});
		shellContext = new PayloadShellContext(androidContext, shell, mauiContext);
		renderer = new ShellSectionRenderer(shellContext)
		{
			ShellSection = section
		};
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
