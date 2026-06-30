#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Android.App;
using Android.Content;
using Android.Views;
using Android.Widget;
using AndroidX.DrawerLayout.Widget;
using Microsoft.Maui;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Controls.Platform.Compatibility;
using AToolbar = AndroidX.AppCompat.Widget.Toolbar;
using AView = Android.Views.View;

namespace AndroidShellFlyoutContentRendererDisconnectContextRetentionRepro;

internal static class ReproSession
{
	const int Attempts = 96;
	const int PayloadBytes = 1024 * 1024;

	static readonly MethodInfo FlyoutDisconnectMethod =
		typeof(ShellFlyoutRenderer).GetMethod("Disconnect", BindingFlags.Instance | BindingFlags.NonPublic)
		?? throw new MissingMethodException(typeof(ShellFlyoutRenderer).FullName, "Disconnect");

	static readonly FieldInfo FlyoutContentField =
		typeof(ShellFlyoutRenderer).GetField("_flyoutContent", BindingFlags.Instance | BindingFlags.NonPublic)
		?? throw new MissingFieldException(typeof(ShellFlyoutRenderer).FullName, "_flyoutContent");

	static readonly FieldInfo TemplatedShellContextField =
		typeof(ShellFlyoutTemplatedContentRenderer).GetField("_shellContext", BindingFlags.Instance | BindingFlags.NonPublic)
		?? throw new MissingFieldException(typeof(ShellFlyoutTemplatedContentRenderer).FullName, "_shellContext");

	public static async Task<ReproReport> RunAsync(Activity activity)
	{
		await Task.Yield();

		ForceFullGc();
		var baseline = GC.GetTotalMemory(forceFullCollection: true);

		var control = await RunScenarioAsync(
			activity,
			"control: clear child ShellFlyoutTemplatedContentRenderer._shellContext after parent disconnect",
			clearChildContext: true);

		var current = await RunScenarioAsync(
			activity,
			"current: parent ShellFlyoutRenderer.Disconnect only",
			clearChildContext: false);

		ForceFullGc();
		var final = GC.GetTotalMemory(forceFullCollection: true);

		return new ReproReport(Attempts, PayloadBytes, control, current, baseline, final);
	}

	static async Task<RunStats> RunScenarioAsync(Activity activity, string name, bool clearChildContext)
	{
		var retainedParents = new List<ShellFlyoutRenderer>(Attempts);
		var parentRefs = new List<WeakReference<ShellFlyoutRenderer>>(Attempts);
		var childRefs = new List<WeakReference<ShellFlyoutTemplatedContentRenderer>>(Attempts);
		var shellRefs = new List<WeakReference<Shell>>(Attempts);
		var shellContextRefs = new List<WeakReference<PayloadShellContext>>(Attempts);
		var mauiContextRefs = new List<WeakReference<IMauiContext>>(Attempts);
		var providerRefs = new List<WeakReference<PayloadServiceProvider>>(Attempts);
		var payloadRefs = new List<PayloadWeakReference>(Attempts);

		for (var i = 0; i < Attempts; i++)
		{
			CreateDisconnectedFlyout(
				activity,
				clearChildContext,
				retainedParents,
				parentRefs,
				childRefs,
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
		GC.KeepAlive(retainedParents);

		var aliveParents = parentRefs.Count(static wr => wr.TryGetTarget(out _));
		var aliveChildren = childRefs.Count(static wr => wr.TryGetTarget(out _));
		var aliveShells = shellRefs.Count(static wr => wr.TryGetTarget(out _));
		var aliveShellContexts = shellContextRefs.Count(static wr => wr.TryGetTarget(out _));
		var aliveMauiContexts = mauiContextRefs.Count(static wr => wr.TryGetTarget(out _));
		var aliveProviders = providerRefs.Count(static wr => wr.TryGetTarget(out _));
		var alivePayloadServices = payloadRefs.Count(static wr => wr.PayloadService.TryGetTarget(out _));
		var alivePayloadByteArrays = payloadRefs.Count(static wr => wr.Bytes.TryGetTarget(out _));
		var parentsWithChild = parentRefs.Count(static wr =>
			wr.TryGetTarget(out var parent) &&
			FlyoutContentField.GetValue(parent) is ShellFlyoutTemplatedContentRenderer);
		var childrenWithShellContext = childRefs.Count(static wr =>
			wr.TryGetTarget(out var child) &&
			TemplatedShellContextField.GetValue(child) is IShellContext);
		var childrenResolvingPayloadService = childRefs.Count(static wr =>
			wr.TryGetTarget(out var child) &&
			TemplatedShellContextField.GetValue(child) is PayloadShellContext context &&
			context.MauiContext.Services.GetService(typeof(PayloadService)) is PayloadService);

		return new RunStats(
			name,
			Attempts,
			aliveParents,
			aliveChildren,
			aliveShells,
			aliveShellContexts,
			aliveMauiContexts,
			aliveProviders,
			alivePayloadServices,
			alivePayloadByteArrays,
			parentsWithChild,
			childrenWithShellContext,
			childrenResolvingPayloadService,
			(long)alivePayloadByteArrays * PayloadBytes);
	}

	static void CreateDisconnectedFlyout(
		Activity activity,
		bool clearChildContext,
		List<ShellFlyoutRenderer> retainedParents,
		List<WeakReference<ShellFlyoutRenderer>> parentRefs,
		List<WeakReference<ShellFlyoutTemplatedContentRenderer>> childRefs,
		List<WeakReference<Shell>> shellRefs,
		List<WeakReference<PayloadShellContext>> shellContextRefs,
		List<WeakReference<IMauiContext>> mauiContextRefs,
		List<WeakReference<PayloadServiceProvider>> providerRefs,
		List<PayloadWeakReference> payloadRefs,
		int index)
	{
		var payload = new PayloadService(index, PayloadBytes);
		var provider = new PayloadServiceProvider(payload);
		var mauiContext = new MauiContext(provider, activity);
		var shell = new Shell
		{
			Title = $"Retired Shell {index:0000}",
			BindingContext = payload,
			FlyoutBehavior = FlyoutBehavior.Flyout
		};
		var shellHandler = new FakeShellHandler(activity);
		shellHandler.SetMauiContext(mauiContext);
		shellHandler.SetVirtualView(shell);

		var shellContext = new PayloadShellContext(activity, new DrawerLayout(activity), shell, mauiContext);
		var parentRenderer = new ShellFlyoutRenderer(shellContext, activity);
		((IShellFlyoutRenderer)parentRenderer).AttachFlyout(shellContext, new FrameLayout(activity));

		if (FlyoutContentField.GetValue(parentRenderer) is not ShellFlyoutTemplatedContentRenderer childRenderer)
			throw new InvalidOperationException("ShellFlyoutRenderer did not create a templated flyout content renderer.");

		parentRefs.Add(new WeakReference<ShellFlyoutRenderer>(parentRenderer));
		childRefs.Add(new WeakReference<ShellFlyoutTemplatedContentRenderer>(childRenderer));
		shellRefs.Add(new WeakReference<Shell>(shell));
		shellContextRefs.Add(new WeakReference<PayloadShellContext>(shellContext));
		mauiContextRefs.Add(new WeakReference<IMauiContext>(mauiContext));
		providerRefs.Add(new WeakReference<PayloadServiceProvider>(provider));
		payloadRefs.Add(new PayloadWeakReference(new WeakReference<PayloadService>(payload), new WeakReference<byte[]>(payload.Bytes)));
		retainedParents.Add(parentRenderer);

		FlyoutDisconnectMethod.Invoke(parentRenderer, null);
		shell.Handler = null;
		shellHandler.DisconnectHandler();

		if (clearChildContext)
			TemplatedShellContextField.SetValue(childRenderer, null);
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

	sealed class PayloadShellContext : IShellContext
	{
		public PayloadShellContext(Context androidContext, DrawerLayout drawerLayout, Shell shell, IMauiContext mauiContext)
		{
			AndroidContext = androidContext;
			CurrentDrawerLayout = drawerLayout;
			Shell = shell;
			MauiContext = mauiContext;
		}

		public Context AndroidContext { get; }

		public DrawerLayout CurrentDrawerLayout { get; }

		public Shell Shell { get; }

		public IMauiContext MauiContext { get; }

		public IShellObservableFragment CreateFragmentForPage(Page page) =>
			throw new NotSupportedException("Fragments are not needed for this flyout disconnect repro.");

		public IShellFlyoutContentRenderer CreateShellFlyoutContentRenderer() =>
			new ShellFlyoutTemplatedContentRenderer(this);

		public IShellItemRenderer CreateShellItemRenderer(ShellItem shellItem) =>
			throw new NotSupportedException("Shell item renderers are not needed for this flyout disconnect repro.");

		public IShellSectionRenderer CreateShellSectionRenderer(ShellSection shellSection) =>
			throw new NotSupportedException("Shell section renderers are not needed for this flyout disconnect repro.");

		public IShellToolbarTracker CreateTrackerForToolbar(AToolbar toolbar) =>
			throw new NotSupportedException("Toolbar trackers are not needed for this flyout disconnect repro.");

		public IShellToolbarAppearanceTracker CreateToolbarAppearanceTracker() =>
			throw new NotSupportedException("Toolbar appearance trackers are not needed for this flyout disconnect repro.");

		public IShellTabLayoutAppearanceTracker CreateTabLayoutAppearanceTracker(ShellSection shellSection) =>
			throw new NotSupportedException("Tab layout appearance trackers are not needed for this flyout disconnect repro.");

		public IShellBottomNavViewAppearanceTracker CreateBottomNavViewAppearanceTracker(ShellItem shellItem) =>
			throw new NotSupportedException("Bottom nav appearance trackers are not needed for this flyout disconnect repro.");
	}

	sealed class FakeShellHandler : IViewHandler
	{
		public FakeShellHandler(Context context)
		{
			PlatformView = new FrameLayout(context);
		}

		public object? PlatformView { get; }

		public object? ContainerView => null;

		public bool HasContainer { get; set; }

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

		public Microsoft.Maui.Graphics.Size GetDesiredSize(double widthConstraint, double heightConstraint) =>
			Microsoft.Maui.Graphics.Size.Zero;

		public void PlatformArrange(Microsoft.Maui.Graphics.Rect frame)
		{
		}
	}
}

internal sealed record RunStats(
	string Name,
	int Attempts,
	int AliveParentFlyoutRenderers,
	int AliveChildContentRenderers,
	int AliveShells,
	int AliveShellContexts,
	int AliveMauiContexts,
	int AliveProviders,
	int AlivePayloadServices,
	int AlivePayloadByteArrays,
	int ParentsWithChildContentRenderer,
	int ChildRenderersWithShellContext,
	int ChildRenderersResolvingPayloadService,
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
		Control.AliveParentFlyoutRenderers == Attempts &&
		Control.AliveChildContentRenderers == Attempts &&
		Control.AliveShells == 0 &&
		Control.AliveShellContexts == 0 &&
		Control.AliveMauiContexts == 0 &&
		Control.AliveProviders == 0 &&
		Control.AlivePayloadServices == 0 &&
		Control.AlivePayloadByteArrays == 0 &&
		Control.ParentsWithChildContentRenderer == Attempts &&
		Control.ChildRenderersWithShellContext == 0 &&
		Control.ChildRenderersResolvingPayloadService == 0 &&
		Current.AliveParentFlyoutRenderers == Attempts &&
		Current.AliveChildContentRenderers == Attempts &&
		Current.AliveShells == Attempts &&
		Current.AliveShellContexts == Attempts &&
		Current.AliveMauiContexts == Attempts &&
		Current.AliveProviders == Attempts &&
		Current.AlivePayloadServices == Attempts &&
		Current.AlivePayloadByteArrays == Attempts &&
		Current.ParentsWithChildContentRenderer == Attempts &&
		Current.ChildRenderersWithShellContext == Attempts &&
		Current.ChildRenderersResolvingPayloadService == Attempts;

	public string ToText()
	{
		return string.Join(Environment.NewLine,
			"AndroidShellFlyoutContentRendererDisconnectContextRetentionRepro",
			$"Retained disconnected parent flyout renderers: {Attempts}",
			$"Payload per attempt: {PayloadBytes / 1024 / 1024} MiB",
			"Source path mirrored: ShellFlyoutRenderer.Disconnect() -> ShellFlyoutTemplatedContentRenderer.Disconnect()",
			"Control difference: clear only child ShellFlyoutTemplatedContentRenderer._shellContext after real disconnect",
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
			$"  retained disconnected parent flyout renderers: {stats.Attempts}",
			$"  parent flyout renderers alive after full GC: {stats.AliveParentFlyoutRenderers}/{stats.Attempts}",
			$"  child content renderers alive after full GC: {stats.AliveChildContentRenderers}/{stats.Attempts}",
			$"  Shells alive after full GC: {stats.AliveShells}/{stats.Attempts}",
			$"  ShellContexts alive after full GC: {stats.AliveShellContexts}/{stats.Attempts}",
			$"  MauiContexts alive after full GC: {stats.AliveMauiContexts}/{stats.Attempts}",
			$"  service providers alive after full GC: {stats.AliveProviders}/{stats.Attempts}",
			$"  payload services alive after full GC: {stats.AlivePayloadServices}/{stats.Attempts}",
			$"  payload byte arrays alive after full GC: {stats.AlivePayloadByteArrays}/{stats.Attempts}",
			$"  parent _flyoutContent fields retaining child renderer: {stats.ParentsWithChildContentRenderer}/{stats.Attempts}",
			$"  child renderer _shellContext fields: {stats.ChildRenderersWithShellContext}/{stats.Attempts}",
			$"  child renderers resolving payload service: {stats.ChildRenderersResolvingPayloadService}/{stats.Attempts}",
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
