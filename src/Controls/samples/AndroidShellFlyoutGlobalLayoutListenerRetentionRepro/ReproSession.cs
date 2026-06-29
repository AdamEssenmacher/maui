#nullable enable

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Android.App;
using Android.Content;
using Android.Views;
using Android.Widget;
using AndroidX.DrawerLayout.Widget;
using Google.Android.Material.AppBar;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Maui;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Controls.Platform.Compatibility;
using AToolbar = AndroidX.AppCompat.Widget.Toolbar;
using AView = Android.Views.View;

namespace AndroidShellFlyoutGlobalLayoutListenerRetentionRepro;

internal static class ReproSession
{
	const int Cycles = 48;
	const int PayloadBytesPerContext = 512 * 1024;

	static readonly List<AView> RetainedNativeFlyoutRoots = new();

	static readonly MethodInfo DisconnectMethod =
		typeof(ShellFlyoutTemplatedContentRenderer).GetMethod("Disconnect", BindingFlags.Instance | BindingFlags.NonPublic)
		?? throw new InvalidOperationException("Could not find ShellFlyoutTemplatedContentRenderer.Disconnect().");

	static readonly FieldInfo AppBarField =
		typeof(ShellFlyoutTemplatedContentRenderer).GetField("_appBar", BindingFlags.Instance | BindingFlags.NonPublic)
		?? throw new InvalidOperationException("Could not find ShellFlyoutTemplatedContentRenderer._appBar.");

	public static async Task<ReproReport> RunAsync(Activity activity)
	{
		ForceFullGc();
		var baselineBytes = GC.GetTotalMemory(true);

		var control = await RunScenarioAsync(
			"control: complete deferred flyout layout before disconnect",
			activity,
			completeInitialLayout: true);

		var current = await RunScenarioAsync(
			"current: disconnect before deferred flyout layout listener invalidates",
			activity,
			completeInitialLayout: false);

		ForceFullGc();
		var finalBytes = GC.GetTotalMemory(true);

		GC.KeepAlive(RetainedNativeFlyoutRoots);

		return new ReproReport(
			Cycles,
			PayloadBytesPerContext,
			baselineBytes,
			finalBytes,
			control,
			current);
	}

	static async Task<ScenarioResult> RunScenarioAsync(
		string name,
		Activity activity,
		bool completeInitialLayout)
	{
		var tracked = new List<TrackedCycle>(Cycles);

		for (var i = 0; i < Cycles; i++)
		{
			await CreateDisconnectedFlyoutCycleAsync(activity, i, tracked, completeInitialLayout);

			if (i % 8 == 0)
				await Task.Yield();
		}

		ForceFullGc();

		return ScenarioResult.From(name, tracked);
	}

	static async Task CreateDisconnectedFlyoutCycleAsync(
		Activity activity,
		int cycle,
		List<TrackedCycle> tracked,
		bool completeInitialLayout)
	{
		var payload = new PayloadService(cycle, PayloadBytesPerContext);
		var services = new ServiceCollection()
			.AddSingleton(payload)
			.BuildServiceProvider();
		var mauiContext = new MauiContext(services, activity);

		var shell = new Shell();
		shell.BindingContext = payload;
		var shellHandler = new FakeShellHandler(activity);
		shellHandler.SetMauiContext(mauiContext);
		shellHandler.SetVirtualView(shell);

		var shellContext = new FakeShellContext(activity, new DrawerLayout(activity), shell);
		var renderer = new ShellFlyoutTemplatedContentRenderer(shellContext);
		var rootView = renderer.AndroidView
			?? throw new InvalidOperationException("Shell flyout renderer did not create a native root.");

		if (completeInitialLayout)
		{
			await AttachAndCompleteInitialLayoutAsync(activity, rootView);
		}

		DisconnectAndRemoveNonCandidateRoots(renderer, rootView);
		shell.Handler = null;

		RetainedNativeFlyoutRoots.Add(rootView);
		tracked.Add(TrackedCycle.Create(cycle, rootView, renderer, shell, shellHandler, shellContext, mauiContext, services, payload));
	}

	static async Task AttachAndCompleteInitialLayoutAsync(Activity activity, AView rootView)
	{
		if (activity.Window?.DecorView is not ViewGroup decorView)
			throw new InvalidOperationException("Activity decor view was not available.");

		decorView.AddView(rootView, new ViewGroup.LayoutParams(1, 1));
		var listener = new CountingGlobalLayoutListener(rootView, count: 2);
		rootView.RequestLayout();
		decorView.RequestLayout();
		DispatchGlobalLayout(rootView);
		DispatchGlobalLayout(rootView);

		await listener.Task.WaitAsync(TimeSpan.FromSeconds(5));

		decorView.RemoveView(rootView);
	}

	static void DispatchGlobalLayout(AView rootView)
	{
		var observer = rootView.ViewTreeObserver;
		if (observer is not null && observer.IsAlive)
			observer.DispatchOnGlobalLayout();
	}

	static void DisconnectAndRemoveNonCandidateRoots(ShellFlyoutTemplatedContentRenderer renderer, AView rootView)
	{
		DisconnectMethod.Invoke(renderer, null);

		if (AppBarField.GetValue(renderer) is AppBarLayout appBar)
			appBar.RemoveOnOffsetChangedListener(renderer);

		var layoutChangingProperty = rootView.GetType().GetProperty("LayoutChanging", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
		layoutChangingProperty?.SetValue(rootView, null);
	}

	static void ForceFullGc()
	{
		for (var i = 0; i < 4; i++)
		{
			GC.Collect();
			GC.WaitForPendingFinalizers();
			GC.Collect();
			Java.Lang.JavaSystem.Gc();
			Thread.Sleep(100);
		}
	}

	internal sealed class PayloadService
	{
		public PayloadService(int cycle, int payloadBytes)
		{
			Cycle = cycle;
			PayloadBytes = payloadBytes;
			Payload = CreatePayload(cycle, payloadBytes);
			Tokens = CreateTokens(cycle);
		}

		public int Cycle { get; }

		public int PayloadBytes { get; }

		public byte[] Payload { get; }

		public IReadOnlyList<string> Tokens { get; }
	}

	sealed class CountingGlobalLayoutListener : Java.Lang.Object, ViewTreeObserver.IOnGlobalLayoutListener
	{
		readonly AView _target;
		readonly TaskCompletionSource _completion = new();
		int _remaining;

		public CountingGlobalLayoutListener(AView target, int count)
		{
			_target = target;
			_remaining = count;
			_target.ViewTreeObserver?.AddOnGlobalLayoutListener(this);
		}

		public Task Task => _completion.Task;

		public void OnGlobalLayout()
		{
			_remaining--;
			if (_remaining > 0)
				return;

			var observer = _target.ViewTreeObserver;
			if (observer is not null && observer.IsAlive)
				observer.RemoveOnGlobalLayoutListener(this);

			_completion.TrySetResult();
		}
	}

	static string[] CreateTokens(int cycle)
	{
		var tokens = new string[16];
		for (var i = 0; i < tokens.Length; i++)
			tokens[i] = $"shell-flyout-global-layout-token-{cycle:D4}-{i:D2}";

		return tokens;
	}

	static byte[] CreatePayload(int cycle, int payloadBytes)
	{
		var payload = new byte[payloadBytes];
		for (var i = 0; i < payload.Length; i += 4096)
			payload[i] = (byte)(0x31 + cycle + i);

		return payload;
	}

	internal sealed class FakeShellContext : IShellContext
	{
		public FakeShellContext(Context androidContext, DrawerLayout drawerLayout, Shell shell)
		{
			AndroidContext = androidContext;
			CurrentDrawerLayout = drawerLayout;
			Shell = shell;
		}

		public Context AndroidContext { get; }

		public DrawerLayout CurrentDrawerLayout { get; }

		public Shell Shell { get; }

		public IShellObservableFragment CreateFragmentForPage(Page page) =>
			throw new NotSupportedException("Fragments are not needed for this flyout-content repro.");

		public IShellFlyoutContentRenderer CreateShellFlyoutContentRenderer() =>
			throw new NotSupportedException("Nested flyout renderers are not needed for this repro.");

		public IShellItemRenderer CreateShellItemRenderer(ShellItem shellItem) =>
			throw new NotSupportedException("Shell item renderers are not needed for this repro.");

		public IShellSectionRenderer CreateShellSectionRenderer(ShellSection shellSection) =>
			throw new NotSupportedException("Shell section renderers are not needed for this repro.");

		public IShellToolbarTracker CreateTrackerForToolbar(AToolbar toolbar) =>
			throw new NotSupportedException("Toolbar trackers are not needed for this repro.");

		public IShellToolbarAppearanceTracker CreateToolbarAppearanceTracker() =>
			throw new NotSupportedException("Toolbar appearance trackers are not needed for this repro.");

		public IShellTabLayoutAppearanceTracker CreateTabLayoutAppearanceTracker(ShellSection shellSection) =>
			throw new NotSupportedException("Tab layout appearance trackers are not needed for this repro.");

		public IShellBottomNavViewAppearanceTracker CreateBottomNavViewAppearanceTracker(ShellItem shellItem) =>
			throw new NotSupportedException("Bottom nav appearance trackers are not needed for this repro.");
	}

	internal sealed class FakeShellHandler : IViewHandler
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
		}

		public Microsoft.Maui.Graphics.Size GetDesiredSize(double widthConstraint, double heightConstraint) =>
			Microsoft.Maui.Graphics.Size.Zero;

		public void PlatformArrange(Microsoft.Maui.Graphics.Rect frame)
		{
		}
	}

	internal sealed record TrackedCycle(
		int Cycle,
		WeakReference<AView> NativeRoot,
		WeakReference<ShellFlyoutTemplatedContentRenderer> Renderer,
		WeakReference<Shell> Shell,
		WeakReference<FakeShellHandler> ShellHandler,
		WeakReference<FakeShellContext> ShellContext,
		WeakReference<MauiContext> MauiContext,
		WeakReference<IServiceProvider> ServiceProvider,
		WeakReference<PayloadService> PayloadService,
		WeakReference<byte[]> PayloadBytes,
		long PayloadBytesPerContext)
	{
		public static TrackedCycle Create(
			int cycle,
			AView nativeRoot,
			ShellFlyoutTemplatedContentRenderer renderer,
			Shell shell,
			FakeShellHandler shellHandler,
			FakeShellContext shellContext,
			MauiContext mauiContext,
			IServiceProvider serviceProvider,
			PayloadService payload)
		{
			return new TrackedCycle(
				cycle,
				new WeakReference<AView>(nativeRoot),
				new WeakReference<ShellFlyoutTemplatedContentRenderer>(renderer),
				new WeakReference<Shell>(shell),
				new WeakReference<FakeShellHandler>(shellHandler),
				new WeakReference<FakeShellContext>(shellContext),
				new WeakReference<MauiContext>(mauiContext),
				new WeakReference<IServiceProvider>(serviceProvider),
				new WeakReference<PayloadService>(payload),
				new WeakReference<byte[]>(payload.Payload),
				payload.PayloadBytes);
		}
	}

	internal sealed record ScenarioResult(
		string Name,
		int TrackedCycles,
		int AliveNativeRoots,
		int AliveRenderers,
		int AliveShells,
		int AliveShellHandlers,
		int AliveShellContexts,
		int AliveMauiContexts,
		int AliveServiceProviders,
		int AlivePayloadServices,
		int AlivePayloadByteArrays,
		long RetainedPayloadBytes)
	{
		internal static ScenarioResult From(string name, IReadOnlyList<TrackedCycle> tracked)
		{
			var aliveNativeRoots = 0;
			var aliveRenderers = 0;
			var aliveShells = 0;
			var aliveShellHandlers = 0;
			var aliveShellContexts = 0;
			var aliveMauiContexts = 0;
			var aliveServiceProviders = 0;
			var alivePayloadServices = 0;
			var alivePayloadByteArrays = 0;
			long retainedPayloadBytes = 0;

			foreach (var cycle in tracked)
			{
				if (cycle.NativeRoot.TryGetTarget(out _))
					aliveNativeRoots++;

				if (cycle.Renderer.TryGetTarget(out _))
					aliveRenderers++;

				if (cycle.Shell.TryGetTarget(out _))
					aliveShells++;

				if (cycle.ShellHandler.TryGetTarget(out _))
					aliveShellHandlers++;

				if (cycle.ShellContext.TryGetTarget(out _))
					aliveShellContexts++;

				if (cycle.MauiContext.TryGetTarget(out _))
					aliveMauiContexts++;

				if (cycle.ServiceProvider.TryGetTarget(out _))
					aliveServiceProviders++;

				if (cycle.PayloadService.TryGetTarget(out _))
					alivePayloadServices++;

				if (cycle.PayloadBytes.TryGetTarget(out _))
				{
					alivePayloadByteArrays++;
					retainedPayloadBytes += cycle.PayloadBytesPerContext;
				}
			}

			return new ScenarioResult(
				name,
				tracked.Count,
				aliveNativeRoots,
				aliveRenderers,
				aliveShells,
				aliveShellHandlers,
				aliveShellContexts,
				aliveMauiContexts,
				aliveServiceProviders,
				alivePayloadServices,
				alivePayloadByteArrays,
				retainedPayloadBytes);
		}
	}
}

internal sealed record ReproReport(
	int Cycles,
	int PayloadBytesPerContext,
	long BaselineManagedBytes,
	long FinalManagedBytes,
	ReproSession.ScenarioResult Control,
	ReproSession.ScenarioResult Current)
{
	public bool LeakProved =>
		Control.AliveNativeRoots == Cycles &&
		Current.AliveNativeRoots == Cycles &&
		Control.AliveRenderers == 0 &&
		Control.AliveShells == 0 &&
		Control.AlivePayloadByteArrays == 0 &&
		Current.AliveRenderers == Cycles &&
		Current.AliveShellContexts == Cycles &&
		Current.AlivePayloadByteArrays == Cycles &&
		Current.AliveShells == Cycles;

	public string ToText()
	{
		var heapDeltaMiB = (FinalManagedBytes - BaselineManagedBytes) / 1024d / 1024d;
		var controlMiB = Control.RetainedPayloadBytes / 1024d / 1024d;
		var currentMiB = Current.RetainedPayloadBytes / 1024d / 1024d;

		return string.Join(Environment.NewLine,
			"AndroidShellFlyoutGlobalLayoutListenerRetentionRepro",
			$"Cycles: {Cycles}",
			$"Payload bytes per Shell binding graph: {PayloadBytesPerContext:N0}",
			"Source paths mirrored: ShellFlyoutTemplatedContentRenderer.LoadView, GenericGlobalLayoutListener deferred InitialLoad callback, and ShellFlyoutTemplatedContentRenderer.Disconnect",
			"Retained peers: native ShellFlyoutLayout roots only; AppBar offset listeners and LayoutChanging callbacks are removed in both runs to isolate the deferred global-layout listener",
			$"Baseline managed heap: {BaselineManagedBytes:N0} bytes",
			$"Final managed heap: {FinalManagedBytes:N0} bytes",
			$"Managed heap delta: {heapDeltaMiB:N1} MiB",
			$"Leak proved: {LeakProved}",
			string.Empty,
			Format(Control),
			string.Empty,
			Format(Current),
			string.Empty,
			$"Control retained Shell binding payload: {controlMiB:N1} MiB",
			$"Current retained Shell binding payload: {currentMiB:N1} MiB",
			$"RESULT: {(LeakProved ? "PROVEN" : "NOT PROVEN")}");
	}

	static string Format(ReproSession.ScenarioResult result)
	{
		return string.Join(Environment.NewLine,
			$"Run: {result.Name}",
			$"  tracked cycles: {result.TrackedCycles}",
			$"  alive native flyout roots: {result.AliveNativeRoots}/{result.TrackedCycles}",
			$"  alive flyout renderers: {result.AliveRenderers}/{result.TrackedCycles}",
			$"  alive Shells: {result.AliveShells}/{result.TrackedCycles}",
			$"  alive shell handlers: {result.AliveShellHandlers}/{result.TrackedCycles}",
			$"  alive shell contexts: {result.AliveShellContexts}/{result.TrackedCycles}",
			$"  alive MauiContexts: {result.AliveMauiContexts}/{result.TrackedCycles}",
			$"  alive service providers: {result.AliveServiceProviders}/{result.TrackedCycles}",
			$"  alive payload services: {result.AlivePayloadServices}/{result.TrackedCycles}",
			$"  alive payload byte arrays: {result.AlivePayloadByteArrays}/{result.TrackedCycles}",
			$"  retained payload bytes: {result.RetainedPayloadBytes:N0}");
	}
}
