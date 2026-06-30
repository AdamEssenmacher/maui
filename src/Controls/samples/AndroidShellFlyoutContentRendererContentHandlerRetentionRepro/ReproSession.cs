#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Android.Content;
using Android.Views;
using Android.Widget;
using AndroidX.DrawerLayout.Widget;
using Microsoft.Maui;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Controls.Platform.Compatibility;
using Microsoft.Maui.Graphics;
using AToolbar = AndroidX.AppCompat.Widget.Toolbar;

namespace AndroidShellFlyoutContentRendererContentHandlerRetentionRepro;

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

	static readonly MethodInfo UpdateFlyoutContentMethod =
		typeof(ShellFlyoutTemplatedContentRenderer).GetMethod("UpdateFlyoutContent", BindingFlags.Instance | BindingFlags.NonPublic)
		?? throw new MissingMethodException(typeof(ShellFlyoutTemplatedContentRenderer).FullName, "UpdateFlyoutContent");

	static readonly FieldInfo TemplatedShellContextField =
		typeof(ShellFlyoutTemplatedContentRenderer).GetField("_shellContext", BindingFlags.Instance | BindingFlags.NonPublic)
		?? throw new MissingFieldException(typeof(ShellFlyoutTemplatedContentRenderer).FullName, "_shellContext");

	static readonly FieldInfo TemplatedContentViewField =
		typeof(ShellFlyoutTemplatedContentRenderer).GetField("_contentView", BindingFlags.Instance | BindingFlags.NonPublic)
		?? throw new MissingFieldException(typeof(ShellFlyoutTemplatedContentRenderer).FullName, "_contentView");

	static readonly Type ShellViewRendererType = TemplatedContentViewField.FieldType;

	static readonly PropertyInfo ShellViewRendererHandlerProperty =
		ShellViewRendererType.GetProperty("Handler", BindingFlags.Instance | BindingFlags.Public)
		?? throw new MissingMemberException(ShellViewRendererType.FullName, "Handler");

	static readonly FieldInfo ShellViewRendererHandlerField =
		ShellViewRendererType.GetField("<Handler>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)
		?? throw new MissingFieldException(ShellViewRendererType.FullName, "<Handler>k__BackingField");

	static readonly FieldInfo ShellViewRendererMauiContextField =
		ShellViewRendererType.GetField("_mauiContext", BindingFlags.Instance | BindingFlags.NonPublic)
		?? throw new MissingFieldException(ShellViewRendererType.FullName, "_mauiContext");

	public static async Task<ReproReport> RunAsync(IMauiContext hostContext)
	{
		await Task.Yield();

		ForceFullGc();
		var baseline = GC.GetTotalMemory(forceFullCollection: true);

		var control = await RunScenarioAsync(
			hostContext,
			"control: clear stale ShellViewRenderer.Handler after disconnect",
			clearHostedHandler: true);

		var current = await RunScenarioAsync(
			hostContext,
			"current: ShellFlyoutTemplatedContentRenderer.Disconnect only",
			clearHostedHandler: false);

		ForceFullGc();
		var final = GC.GetTotalMemory(forceFullCollection: true);

		return new ReproReport(Attempts, PayloadBytes, control, current, baseline, final);
	}

	static async Task<RunStats> RunScenarioAsync(IMauiContext hostContext, string name, bool clearHostedHandler)
	{
		var retainedParents = new List<ShellFlyoutRenderer>(Attempts);
		var parentRefs = new List<WeakReference<ShellFlyoutRenderer>>(Attempts);
		var childRefs = new List<WeakReference<ShellFlyoutTemplatedContentRenderer>>(Attempts);
		var shellViewRendererRefs = new List<WeakReference<object>>(Attempts);
		var shellRefs = new List<WeakReference<Shell>>(Attempts);
		var contentViewRefs = new List<WeakReference<BoxView>>(Attempts);
		var contentHandlerRefs = new List<WeakReference<IViewHandler>>(Attempts);
		var mauiContextRefs = new List<WeakReference<IMauiContext>>(Attempts);
		var providerRefs = new List<WeakReference<PayloadServiceProvider>>(Attempts);
		var payloadRefs = new List<PayloadWeakReference>(Attempts);

		for (var i = 0; i < Attempts; i++)
		{
			CreateDisconnectedFlyoutWithCustomContent(
				hostContext,
				clearHostedHandler,
				retainedParents,
				parentRefs,
				childRefs,
				shellViewRendererRefs,
				shellRefs,
				contentViewRefs,
				contentHandlerRefs,
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
		var aliveShellViewRenderers = shellViewRendererRefs.Count(static wr => wr.TryGetTarget(out _));
		var aliveShells = shellRefs.Count(static wr => wr.TryGetTarget(out _));
		var aliveContentViews = contentViewRefs.Count(static wr => wr.TryGetTarget(out _));
		var aliveContentHandlers = contentHandlerRefs.Count(static wr => wr.TryGetTarget(out _));
		var aliveMauiContexts = mauiContextRefs.Count(static wr => wr.TryGetTarget(out _));
		var aliveProviders = providerRefs.Count(static wr => wr.TryGetTarget(out _));
		var alivePayloadServices = payloadRefs.Count(static wr => wr.PayloadService.TryGetTarget(out _));
		var alivePayloadByteArrays = payloadRefs.Count(static wr => wr.Bytes.TryGetTarget(out _));
		var parentsWithChild = parentRefs.Count(static wr =>
			wr.TryGetTarget(out var parent) &&
			FlyoutContentField.GetValue(parent) is ShellFlyoutTemplatedContentRenderer);
		var childRenderersWithContentView = childRefs.Count(static wr =>
			wr.TryGetTarget(out var child) &&
			TemplatedContentViewField.GetValue(child) is not null);
		var childRenderersWithShellContext = childRefs.Count(static wr =>
			wr.TryGetTarget(out var child) &&
			TemplatedShellContextField.GetValue(child) is IShellContext);
		var nestedRenderersWithMauiContext = shellViewRendererRefs.Count(static wr =>
			wr.TryGetTarget(out var shellViewRenderer) &&
			ShellViewRendererMauiContextField.GetValue(shellViewRenderer) is IMauiContext);
		var nestedRenderersWithHandler = shellViewRendererRefs.Count(static wr =>
			wr.TryGetTarget(out var shellViewRenderer) &&
			ShellViewRendererHandlerProperty.GetValue(shellViewRenderer) is IViewHandler);
		var nestedHandlersWithMauiContext = shellViewRendererRefs.Count(static wr =>
			wr.TryGetTarget(out var shellViewRenderer) &&
			ShellViewRendererHandlerProperty.GetValue(shellViewRenderer) is IViewHandler handler &&
			handler.MauiContext is IMauiContext);
		var nestedHandlersResolvingPayloadService = shellViewRendererRefs.Count(static wr =>
			wr.TryGetTarget(out var shellViewRenderer) &&
			ShellViewRendererHandlerProperty.GetValue(shellViewRenderer) is IViewHandler handler &&
			handler.MauiContext?.Services.GetService(typeof(PayloadService)) is PayloadService);

		return new RunStats(
			name,
			Attempts,
			aliveParents,
			aliveChildren,
			aliveShellViewRenderers,
			aliveShells,
			aliveContentViews,
			aliveContentHandlers,
			aliveMauiContexts,
			aliveProviders,
			alivePayloadServices,
			alivePayloadByteArrays,
			parentsWithChild,
			childRenderersWithContentView,
			childRenderersWithShellContext,
			nestedRenderersWithMauiContext,
			nestedRenderersWithHandler,
			nestedHandlersWithMauiContext,
			nestedHandlersResolvingPayloadService,
			(long)alivePayloadByteArrays * PayloadBytes);
	}

	static void CreateDisconnectedFlyoutWithCustomContent(
		IMauiContext hostContext,
		bool clearHostedHandler,
		List<ShellFlyoutRenderer> retainedParents,
		List<WeakReference<ShellFlyoutRenderer>> parentRefs,
		List<WeakReference<ShellFlyoutTemplatedContentRenderer>> childRefs,
		List<WeakReference<object>> shellViewRendererRefs,
		List<WeakReference<Shell>> shellRefs,
		List<WeakReference<BoxView>> contentViewRefs,
		List<WeakReference<IViewHandler>> contentHandlerRefs,
		List<WeakReference<IMauiContext>> mauiContextRefs,
		List<WeakReference<PayloadServiceProvider>> providerRefs,
		List<PayloadWeakReference> payloadRefs,
		int index)
	{
		var androidContext = hostContext.Context ?? throw new InvalidOperationException("Android context is not available.");
		var payload = new PayloadService(index, PayloadBytes);
		var provider = new PayloadServiceProvider(hostContext.Services, payload);
		var mauiContext = new MauiContext(provider, androidContext);
		var flyoutContent = new BoxView
		{
			WidthRequest = 64,
			HeightRequest = 64,
			Color = Colors.CadetBlue
		};
		var shell = new Shell
		{
			Title = $"Retired Shell {index:0000}",
			FlyoutBehavior = FlyoutBehavior.Flyout,
			FlyoutContent = flyoutContent
		};
		var shellHandler = new FakeShellHandler(androidContext);
		shellHandler.SetMauiContext(mauiContext);
		shellHandler.SetVirtualView(shell);

		var shellContext = new PayloadShellContext(androidContext, new DrawerLayout(androidContext), shell, mauiContext);
		var parentRenderer = new ShellFlyoutRenderer(shellContext, androidContext);
		((IShellFlyoutRenderer)parentRenderer).AttachFlyout(shellContext, new FrameLayout(androidContext));

		if (FlyoutContentField.GetValue(parentRenderer) is not ShellFlyoutTemplatedContentRenderer childRenderer)
			throw new InvalidOperationException("ShellFlyoutRenderer did not create a templated flyout content renderer.");

		UpdateFlyoutContentMethod.Invoke(childRenderer, null);

		var shellViewRenderer = TemplatedContentViewField.GetValue(childRenderer)
			?? throw new InvalidOperationException("ShellFlyoutTemplatedContentRenderer did not create a ShellViewRenderer.");

		if (ShellViewRendererHandlerProperty.GetValue(shellViewRenderer) is not IViewHandler contentHandler)
			throw new InvalidOperationException("ShellViewRenderer did not retain the hosted content handler before disconnect.");

		parentRefs.Add(new WeakReference<ShellFlyoutRenderer>(parentRenderer));
		childRefs.Add(new WeakReference<ShellFlyoutTemplatedContentRenderer>(childRenderer));
		shellViewRendererRefs.Add(new WeakReference<object>(shellViewRenderer));
		shellRefs.Add(new WeakReference<Shell>(shell));
		contentViewRefs.Add(new WeakReference<BoxView>(flyoutContent));
		contentHandlerRefs.Add(new WeakReference<IViewHandler>(contentHandler));
		mauiContextRefs.Add(new WeakReference<IMauiContext>(mauiContext));
		providerRefs.Add(new WeakReference<PayloadServiceProvider>(provider));
		payloadRefs.Add(new PayloadWeakReference(new WeakReference<PayloadService>(payload), new WeakReference<byte[]>(payload.Bytes)));
		retainedParents.Add(parentRenderer);

		FlyoutDisconnectMethod.Invoke(parentRenderer, null);
		shell.Handler = null;
		shellHandler.DisconnectHandler();

		TemplatedShellContextField.SetValue(childRenderer, null);
		ShellViewRendererMauiContextField.SetValue(shellViewRenderer, null);

		if (clearHostedHandler)
			ShellViewRendererHandlerField.SetValue(shellViewRenderer, null);
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
			throw new NotSupportedException("Fragments are not needed for this flyout content-handler repro.");

		public IShellFlyoutContentRenderer CreateShellFlyoutContentRenderer() =>
			new ShellFlyoutTemplatedContentRenderer(this);

		public IShellItemRenderer CreateShellItemRenderer(ShellItem shellItem) =>
			throw new NotSupportedException("Shell item renderers are not needed for this flyout content-handler repro.");

		public IShellSectionRenderer CreateShellSectionRenderer(ShellSection shellSection) =>
			throw new NotSupportedException("Shell section renderers are not needed for this flyout content-handler repro.");

		public IShellToolbarTracker CreateTrackerForToolbar(AToolbar toolbar) =>
			throw new NotSupportedException("Toolbar trackers are not needed for this flyout content-handler repro.");

		public IShellToolbarAppearanceTracker CreateToolbarAppearanceTracker() =>
			throw new NotSupportedException("Toolbar appearance trackers are not needed for this flyout content-handler repro.");

		public IShellTabLayoutAppearanceTracker CreateTabLayoutAppearanceTracker(ShellSection shellSection) =>
			throw new NotSupportedException("Tab layout appearance trackers are not needed for this flyout content-handler repro.");

		public IShellBottomNavViewAppearanceTracker CreateBottomNavViewAppearanceTracker(ShellItem shellItem) =>
			throw new NotSupportedException("Bottom nav appearance trackers are not needed for this flyout content-handler repro.");
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

		public Size GetDesiredSize(double widthConstraint, double heightConstraint) =>
			Size.Zero;

		public void PlatformArrange(Rect frame)
		{
		}
	}
}

internal sealed record RunStats(
	string Name,
	int Attempts,
	int AliveParentFlyoutRenderers,
	int AliveChildContentRenderers,
	int AliveShellViewRenderers,
	int AliveShells,
	int AliveContentViews,
	int AliveContentHandlers,
	int AliveMauiContexts,
	int AliveProviders,
	int AlivePayloadServices,
	int AlivePayloadByteArrays,
	int ParentsWithChildContentRenderer,
	int ChildRenderersWithContentView,
	int ChildRenderersWithShellContext,
	int NestedRenderersWithMauiContext,
	int NestedRenderersWithHandler,
	int NestedHandlersWithMauiContext,
	int NestedHandlersResolvingPayloadService,
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
		Control.AliveShellViewRenderers == Attempts &&
		Control.AliveShells == 0 &&
		Control.AliveContentViews == 0 &&
		Control.AliveContentHandlers == 0 &&
		Control.AliveMauiContexts == 0 &&
		Control.AliveProviders == 0 &&
		Control.AlivePayloadServices == 0 &&
		Control.AlivePayloadByteArrays == 0 &&
		Control.ParentsWithChildContentRenderer == Attempts &&
		Control.ChildRenderersWithContentView == Attempts &&
		Control.ChildRenderersWithShellContext == 0 &&
		Control.NestedRenderersWithMauiContext == 0 &&
		Control.NestedRenderersWithHandler == 0 &&
		Control.NestedHandlersWithMauiContext == 0 &&
		Control.NestedHandlersResolvingPayloadService == 0 &&
		Current.AliveParentFlyoutRenderers == Attempts &&
		Current.AliveChildContentRenderers == Attempts &&
		Current.AliveShellViewRenderers == Attempts &&
		Current.AliveShells == 0 &&
		Current.AliveContentViews == 0 &&
		Current.AliveContentHandlers == Attempts &&
		Current.AliveMauiContexts == Attempts &&
		Current.AliveProviders == Attempts &&
		Current.AlivePayloadServices == Attempts &&
		Current.AlivePayloadByteArrays == Attempts &&
		Current.ParentsWithChildContentRenderer == Attempts &&
		Current.ChildRenderersWithContentView == Attempts &&
		Current.ChildRenderersWithShellContext == 0 &&
		Current.NestedRenderersWithMauiContext == 0 &&
		Current.NestedRenderersWithHandler == Attempts &&
		Current.NestedHandlersWithMauiContext == Attempts &&
		Current.NestedHandlersResolvingPayloadService == Attempts;

	public string ToText()
	{
		return string.Join(Environment.NewLine,
			"AndroidShellFlyoutContentRendererContentHandlerRetentionRepro",
			$"Retained disconnected parent flyout renderers: {Attempts}",
			$"Payload per attempt: {PayloadBytes / 1024 / 1024} MiB",
			"Source path mirrored: ShellFlyoutTemplatedContentRenderer.UpdateFlyoutContent() -> ShellFlyoutTemplatedContentRenderer.Disconnect()",
			"Non-candidate fields cleared in both runs: ShellFlyoutTemplatedContentRenderer._shellContext and ShellViewRenderer._mauiContext",
			"Control difference: clear only nested ShellViewRenderer.Handler after real disconnect",
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
			$"  ShellViewRenderers alive after full GC: {stats.AliveShellViewRenderers}/{stats.Attempts}",
			$"  Shells alive after full GC: {stats.AliveShells}/{stats.Attempts}",
			$"  hosted FlyoutContent views alive after full GC: {stats.AliveContentViews}/{stats.Attempts}",
			$"  hosted FlyoutContent handlers alive after full GC: {stats.AliveContentHandlers}/{stats.Attempts}",
			$"  MauiContexts alive after full GC: {stats.AliveMauiContexts}/{stats.Attempts}",
			$"  service providers alive after full GC: {stats.AliveProviders}/{stats.Attempts}",
			$"  payload services alive after full GC: {stats.AlivePayloadServices}/{stats.Attempts}",
			$"  payload byte arrays alive after full GC: {stats.AlivePayloadByteArrays}/{stats.Attempts}",
			$"  parent _flyoutContent fields retaining child renderer: {stats.ParentsWithChildContentRenderer}/{stats.Attempts}",
			$"  child renderer _contentView fields: {stats.ChildRenderersWithContentView}/{stats.Attempts}",
			$"  child renderer _shellContext fields: {stats.ChildRenderersWithShellContext}/{stats.Attempts}",
			$"  nested ShellViewRenderer._mauiContext fields: {stats.NestedRenderersWithMauiContext}/{stats.Attempts}",
			$"  nested ShellViewRenderer.Handler fields: {stats.NestedRenderersWithHandler}/{stats.Attempts}",
			$"  nested handlers with MauiContext: {stats.NestedHandlersWithMauiContext}/{stats.Attempts}",
			$"  nested handlers resolving payload service: {stats.NestedHandlersResolvingPayloadService}/{stats.Attempts}",
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
