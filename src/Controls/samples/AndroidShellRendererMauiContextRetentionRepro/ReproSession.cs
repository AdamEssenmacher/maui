#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Android.App;
using Android.Content;
using Microsoft.Maui;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Controls.Handlers.Compatibility;
using Microsoft.Maui.Controls.Platform.Compatibility;
using AFragment = AndroidX.Fragment.App.Fragment;
using AFragmentManager = AndroidX.Fragment.App.FragmentManager;
using AView = Android.Views.View;

namespace AndroidShellRendererMauiContextRetentionRepro;

internal static class ReproSession
{
	const int Attempts = 96;
	const int PayloadBytes = 1024 * 1024;

	static readonly FieldInfo MauiContextField =
		typeof(ShellRenderer).GetField("_mauiContext", BindingFlags.Instance | BindingFlags.NonPublic)
		?? throw new MissingFieldException(typeof(ShellRenderer).FullName, "_mauiContext");

	static readonly FieldInfo FlyoutViewField =
		typeof(ShellRenderer).GetField("_flyoutView", BindingFlags.Instance | BindingFlags.NonPublic)
		?? throw new MissingFieldException(typeof(ShellRenderer).FullName, "_flyoutView");

	static readonly FieldInfo FrameLayoutField =
		typeof(ShellRenderer).GetField("_frameLayout", BindingFlags.Instance | BindingFlags.NonPublic)
		?? throw new MissingFieldException(typeof(ShellRenderer).FullName, "_frameLayout");

	static readonly FieldInfo CurrentViewField =
		typeof(ShellRenderer).GetField("_currentView", BindingFlags.Instance | BindingFlags.NonPublic)
		?? throw new MissingFieldException(typeof(ShellRenderer).FullName, "_currentView");

	static readonly PropertyInfo ElementProperty =
		typeof(ShellRenderer).GetProperty("Element", BindingFlags.Instance | BindingFlags.NonPublic)
		?? throw new MissingMemberException(typeof(ShellRenderer).FullName, "Element");

	public static async Task<ReproReport> RunAsync(Activity activity)
	{
		await Task.Yield();

		ForceFullGc();
		var baseline = GC.GetTotalMemory(forceFullCollection: true);

		var control = await RunScenarioAsync(
			activity,
			"control: clear ShellRenderer._mauiContext after disconnect",
			clearMauiContext: true);

		var current = await RunScenarioAsync(
			activity,
			"current: ShellRenderer.DisconnectHandler only",
			clearMauiContext: false);

		ForceFullGc();
		var final = GC.GetTotalMemory(forceFullCollection: true);

		return new ReproReport(Attempts, PayloadBytes, control, current, baseline, final);
	}

	static async Task<RunStats> RunScenarioAsync(Activity activity, string name, bool clearMauiContext)
	{
		var retainedRenderers = new List<ShellRenderer>(Attempts);
		var rendererRefs = new List<WeakReference<ShellRenderer>>(Attempts);
		var shellRefs = new List<WeakReference<Shell>>(Attempts);
		var mauiContextRefs = new List<WeakReference<IMauiContext>>(Attempts);
		var providerRefs = new List<WeakReference<PayloadServiceProvider>>(Attempts);
		var payloadRefs = new List<PayloadWeakReference>(Attempts);

		for (var i = 0; i < Attempts; i++)
		{
			CreateDisconnectedShellRenderer(
				activity,
				clearMauiContext,
				retainedRenderers,
				rendererRefs,
				shellRefs,
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
		var aliveShells = shellRefs.Count(static wr => wr.TryGetTarget(out _));
		var aliveMauiContexts = mauiContextRefs.Count(static wr => wr.TryGetTarget(out _));
		var aliveProviders = providerRefs.Count(static wr => wr.TryGetTarget(out _));
		var alivePayloadServices = payloadRefs.Count(static wr => wr.PayloadService.TryGetTarget(out _));
		var alivePayloadByteArrays = payloadRefs.Count(static wr => wr.Bytes.TryGetTarget(out _));
		var renderersWithMauiContext = rendererRefs.Count(static wr =>
			wr.TryGetTarget(out var renderer) &&
			MauiContextField.GetValue(renderer) is IMauiContext);
		var renderersResolvingPayloadService = rendererRefs.Count(static wr =>
			wr.TryGetTarget(out var renderer) &&
			MauiContextField.GetValue(renderer) is IMauiContext context &&
			context.Services.GetService(typeof(PayloadService)) is PayloadService);
		var renderersWithFlyoutView = rendererRefs.Count(static wr =>
			wr.TryGetTarget(out var renderer) &&
			FlyoutViewField.GetValue(renderer) is not null);
		var renderersWithFrameLayout = rendererRefs.Count(static wr =>
			wr.TryGetTarget(out var renderer) &&
			FrameLayoutField.GetValue(renderer) is not null);
		var renderersWithCurrentView = rendererRefs.Count(static wr =>
			wr.TryGetTarget(out var renderer) &&
			CurrentViewField.GetValue(renderer) is not null);
		var renderersWithElement = rendererRefs.Count(static wr =>
			wr.TryGetTarget(out var renderer) &&
			ElementProperty.GetValue(renderer) is not null);

		return new RunStats(
			name,
			Attempts,
			aliveRenderers,
			aliveShells,
			aliveMauiContexts,
			aliveProviders,
			alivePayloadServices,
			alivePayloadByteArrays,
			renderersWithMauiContext,
			renderersResolvingPayloadService,
			renderersWithFlyoutView,
			renderersWithFrameLayout,
			renderersWithCurrentView,
			renderersWithElement,
			(long)alivePayloadByteArrays * PayloadBytes);
	}

	static void CreateDisconnectedShellRenderer(
		Activity activity,
		bool clearMauiContext,
		List<ShellRenderer> retainedRenderers,
		List<WeakReference<ShellRenderer>> rendererRefs,
		List<WeakReference<Shell>> shellRefs,
		List<WeakReference<IMauiContext>> mauiContextRefs,
		List<WeakReference<PayloadServiceProvider>> providerRefs,
		List<PayloadWeakReference> payloadRefs,
		int index)
	{
		var payload = new PayloadService(index, PayloadBytes);
		var provider = new PayloadServiceProvider(payload);
		var mauiContext = new MauiContext(provider, activity);
		var shell = CreateShell(index, payload);
		var renderer = new TestShellRenderer(activity);
		var handler = (IElementHandler)renderer;

		handler.SetMauiContext(mauiContext);
		shell.Handler = (IViewHandler)handler;
		handler.SetVirtualView(shell);
		CurrentViewField.SetValue(renderer, new FakeShellItemRenderer());

		rendererRefs.Add(new WeakReference<ShellRenderer>(renderer));
		shellRefs.Add(new WeakReference<Shell>(shell));
		mauiContextRefs.Add(new WeakReference<IMauiContext>(mauiContext));
		providerRefs.Add(new WeakReference<PayloadServiceProvider>(provider));
		payloadRefs.Add(new PayloadWeakReference(new WeakReference<PayloadService>(payload), new WeakReference<byte[]>(payload.Bytes)));
		retainedRenderers.Add(renderer);

		handler.DisconnectHandler();
		shell.Handler = null;

		// Clear non-candidate child/native roots in both runs. The current/control delta is only _mauiContext.
		FlyoutViewField.SetValue(renderer, null);
		FrameLayoutField.SetValue(renderer, null);
		CurrentViewField.SetValue(renderer, null);

		if (clearMauiContext)
			MauiContextField.SetValue(renderer, null);
	}

	static Shell CreateShell(int index, PayloadService payload)
	{
		var shell = new Shell
		{
			Title = $"Retired Shell {index:0000}",
			BindingContext = payload,
			FlyoutBehavior = FlyoutBehavior.Disabled
		};

		shell.Items.Add(new ShellContent
		{
			Title = $"Root {index:0000}",
			Content = new ContentPage { Title = $"Page {index:0000}" }
		});

		return shell;
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
		readonly PayloadService _payload;

		public PayloadServiceProvider(PayloadService payload)
		{
			_payload = payload;
		}

		public object? GetService(Type serviceType)
		{
			if (serviceType == typeof(PayloadService))
				return _payload;

			return null;
		}
	}

	sealed class TestShellRenderer : ShellRenderer
	{
		public TestShellRenderer(Context context)
			: base(context)
		{
		}

		protected override void SwitchFragment(AFragmentManager manager, AView targetView, ShellItem newItem, bool animate = true)
		{
		}
	}

	sealed class FakeShellItemRenderer : IShellItemRenderer
	{
		public AFragment Fragment => throw new NotSupportedException("The fake current item renderer is only used for disconnect cleanup.");

		public ShellItem ShellItem { get; set; } = new ShellContent();

		public event EventHandler? Destroyed;

		public void Dispose()
		{
			Destroyed?.Invoke(this, EventArgs.Empty);
			Destroyed = null;
			ShellItem = null!;
		}
	}
}

internal sealed record RunStats(
	string Name,
	int Attempts,
	int AliveRenderers,
	int AliveShells,
	int AliveMauiContexts,
	int AliveProviders,
	int AlivePayloadServices,
	int AlivePayloadByteArrays,
	int RenderersWithMauiContext,
	int RenderersResolvingPayloadService,
	int RenderersWithFlyoutView,
	int RenderersWithFrameLayout,
	int RenderersWithCurrentView,
	int RenderersWithElement,
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
		Control.AliveRenderers == Attempts &&
		Control.AliveShells == 0 &&
		Control.AliveMauiContexts == 0 &&
		Control.AliveProviders == 0 &&
		Control.AlivePayloadServices == 0 &&
		Control.AlivePayloadByteArrays == 0 &&
		Control.RenderersWithMauiContext == 0 &&
		Control.RenderersResolvingPayloadService == 0 &&
		Control.RenderersWithFlyoutView == 0 &&
		Control.RenderersWithFrameLayout == 0 &&
		Control.RenderersWithCurrentView == 0 &&
		Control.RenderersWithElement == 0 &&
		Current.AliveRenderers == Attempts &&
		Current.AliveShells == 0 &&
		Current.AliveMauiContexts == Attempts &&
		Current.AliveProviders == Attempts &&
		Current.AlivePayloadServices == Attempts &&
		Current.AlivePayloadByteArrays == Attempts &&
		Current.RenderersWithMauiContext == Attempts &&
		Current.RenderersResolvingPayloadService == Attempts &&
		Current.RenderersWithFlyoutView == 0 &&
		Current.RenderersWithFrameLayout == 0 &&
		Current.RenderersWithCurrentView == 0 &&
		Current.RenderersWithElement == 0;

	public string ToText()
	{
		return string.Join(Environment.NewLine,
			"AndroidShellRendererMauiContextRetentionRepro",
			$"Retained disconnected ShellRenderers: {Attempts}",
			$"Payload per attempt: {PayloadBytes / 1024 / 1024} MiB",
			"Source path mirrored: ShellRenderer.DisconnectHandler()",
			"Non-candidate fields cleared in both runs: _flyoutView, _frameLayout, _currentView; Shell.Handler is also cleared",
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
			$"  retained disconnected ShellRenderers: {stats.Attempts}",
			$"  ShellRenderers alive after full GC: {stats.AliveRenderers}/{stats.Attempts}",
			$"  Shells alive after full GC: {stats.AliveShells}/{stats.Attempts}",
			$"  MauiContexts alive after full GC: {stats.AliveMauiContexts}/{stats.Attempts}",
			$"  service providers alive after full GC: {stats.AliveProviders}/{stats.Attempts}",
			$"  payload services alive after full GC: {stats.AlivePayloadServices}/{stats.Attempts}",
			$"  payload byte arrays alive after full GC: {stats.AlivePayloadByteArrays}/{stats.Attempts}",
			$"  ShellRenderer _mauiContext fields: {stats.RenderersWithMauiContext}/{stats.Attempts}",
			$"  ShellRenderers resolving payload service: {stats.RenderersResolvingPayloadService}/{stats.Attempts}",
			$"  ShellRenderer _flyoutView fields: {stats.RenderersWithFlyoutView}/{stats.Attempts}",
			$"  ShellRenderer _frameLayout fields: {stats.RenderersWithFrameLayout}/{stats.Attempts}",
			$"  ShellRenderer _currentView fields: {stats.RenderersWithCurrentView}/{stats.Attempts}",
			$"  ShellRenderer Element properties: {stats.RenderersWithElement}/{stats.Attempts}",
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
