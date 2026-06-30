#nullable enable

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Android.Content;
using Android.Views;
using AndroidX.AppCompat.App;
using AndroidX.AppCompat.Widget;
using AndroidX.CoordinatorLayout.Widget;
using AndroidX.DrawerLayout.Widget;
using AndroidX.Fragment.App;
using AndroidX.ViewPager2.Adapter;
using AndroidX.ViewPager2.Widget;
using Google.Android.Material.Tabs;
using Microsoft.Maui;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Controls.Platform.Compatibility;
using Microsoft.Maui.Graphics;
using Rect = Microsoft.Maui.Graphics.Rect;
using Size = Microsoft.Maui.Graphics.Size;
using AToolbar = AndroidX.AppCompat.Widget.Toolbar;
using AView = Android.Views.View;

namespace AndroidShellSectionViewPagerListenerRetentionRepro;

internal static class ReproSession
{
	const int Cycles = 64;
	const int PayloadBytesPerCycle = 1024 * 1024;

	static readonly List<ViewPager2> RetainedViewPagers = new();
	static readonly List<TabLayout> RetainedTabLayouts = new();

	public static Task<ReproReport> RunAsync(IMauiContext baseContext)
	{
		RetainedViewPagers.Clear();
		RetainedTabLayouts.Clear();

		ForceFullGc();
		var baselineBytes = GC.GetTotalMemory(forceFullCollection: true);

		var control = RunScenario(
			"control: native pager/tab peers retained with stateless strategy and callback",
			baseContext,
			useCurrentShellSectionRendererRegistrations: false);

		var current = RunScenario(
			"current: native pager/tab peers retain ShellSectionRenderer strategy and page callback",
			baseContext,
			useCurrentShellSectionRendererRegistrations: true);

		ForceFullGc();
		GC.KeepAlive(RetainedViewPagers);
		GC.KeepAlive(RetainedTabLayouts);
		var finalBytes = GC.GetTotalMemory(forceFullCollection: true);

		return Task.FromResult(new ReproReport(
			Cycles,
			PayloadBytesPerCycle,
			baselineBytes,
			finalBytes,
			control,
			current));
	}

	static ScenarioResult RunScenario(
		string name,
		IMauiContext baseContext,
		bool useCurrentShellSectionRendererRegistrations)
	{
		var tracked = new List<TrackedCycle>(Cycles);

		for (var i = 0; i < Cycles; i++)
			CreateCycle(baseContext, i, tracked, useCurrentShellSectionRendererRegistrations);

		ForceFullGc();
		return ScenarioResult.From(name, tracked);
	}

	static void CreateCycle(
		IMauiContext baseContext,
		int cycle,
		List<TrackedCycle> tracked,
		bool useCurrentShellSectionRendererRegistrations)
	{
		var activity = Microsoft.Maui.ApplicationModel.Platform.CurrentActivity as AppCompatActivity
			?? throw new InvalidOperationException("The current activity is not an AppCompatActivity.");
		var androidContext = baseContext.Context
			?? activity
			?? Android.App.Application.Context
			?? throw new InvalidOperationException("No Android context is available.");

		var payloadService = new PayloadService(cycle, PayloadBytesPerCycle);
		var payloadProvider = new PayloadServiceProvider(baseContext.Services, payloadService);
		var cycleContext = new MauiContext(payloadProvider, androidContext);

		var shell = new Shell();
		var shellHandler = new PayloadShellHandler(cycleContext, shell);
		shell.Handler = shellHandler;

		var shellContext = new FakeShellContext(androidContext, shell);
		var renderer = new ShellSectionRenderer(shellContext)
		{
			ShellSection = CreateShellSection(cycle)
		};

		var root = new CoordinatorLayout(androidContext);
		var tabLayout = new TabLayout(androidContext);
		var adapter = new NoopFragmentStateAdapter(activity!);
		var strategy = useCurrentShellSectionRendererRegistrations
			? (TabLayoutMediator.ITabConfigurationStrategy)renderer
			: new StatelessTabConfigurationStrategy(cycle);
		var callback = useCurrentShellSectionRendererRegistrations
			? CreateCurrentViewPagerPageChangedCallback(renderer)
			: new StatelessPageChangeCallback();

		var viewPager = CreateShellViewPager(root.Context!, root, tabLayout, strategy, adapter, callback);

		// Mirror the meaningful ShellSectionRenderer.Destroy() cleanup that is relevant to
		// this proof: adapter ownership is released, but the registered native callback and
		// TabLayoutMediator are not unregistered/detached by MAUI's current source path.
		viewPager.Adapter = null;
		adapter.Dispose();
		root.RemoveView(viewPager);

		renderer.Dispose();

		RetainedViewPagers.Add(viewPager);
		RetainedTabLayouts.Add(tabLayout);
		tracked.Add(TrackedCycle.Create(
			cycle,
			viewPager,
			tabLayout,
			renderer,
			shell,
			shellHandler,
			shellContext,
			cycleContext,
			payloadProvider,
			payloadService,
			payloadService.Payload,
			useCurrentShellSectionRendererRegistrations));
	}

	static ShellSection CreateShellSection(int cycle)
	{
		var section = new ShellSection { Title = $"Orders {cycle + 1:0000}" };
		section.Items.Add(new ShellContent { Title = $"Queue {cycle + 1:0000}" });
		return section;
	}

	static ViewPager2.OnPageChangeCallback CreateCurrentViewPagerPageChangedCallback(ShellSectionRenderer renderer)
	{
		var nestedType = typeof(ShellSectionRenderer).GetNestedType("ViewPagerPageChanged", BindingFlags.NonPublic)
			?? throw new MissingMemberException(typeof(ShellSectionRenderer).FullName, "ViewPagerPageChanged");
		return (ViewPager2.OnPageChangeCallback)(Activator.CreateInstance(
			nestedType,
			BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
			binder: null,
			args: new object[] { renderer },
			culture: null)
			?? throw new InvalidOperationException("Could not create ShellSectionRenderer.ViewPagerPageChanged."));
	}

	static ViewPager2 CreateShellViewPager(
		Context context,
		CoordinatorLayout root,
		TabLayout tabLayout,
		TabLayoutMediator.ITabConfigurationStrategy strategy,
		FragmentStateAdapter adapter,
		ViewPager2.OnPageChangeCallback callback)
	{
		var type = typeof(MauiContext).Assembly.GetType("Microsoft.Maui.PlatformInterop")
			?? throw new MissingMemberException("Microsoft.Maui.PlatformInterop");
		var method = type.GetMethod(
			"CreateShellViewPager",
			BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic,
			binder: null,
			types: new[]
			{
				typeof(Context),
				typeof(CoordinatorLayout),
				typeof(TabLayout),
				typeof(TabLayoutMediator.ITabConfigurationStrategy),
				typeof(FragmentStateAdapter),
				typeof(ViewPager2.OnPageChangeCallback)
			},
			modifiers: null)
			?? throw new MissingMethodException(type.FullName, "CreateShellViewPager");

		return (ViewPager2)(method.Invoke(null, new object?[] { context, root, tabLayout, strategy, adapter, callback })
			?? throw new InvalidOperationException("PlatformInterop.CreateShellViewPager returned null."));
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

	sealed class NoopFragmentStateAdapter : FragmentStateAdapter
	{
		public NoopFragmentStateAdapter(FragmentActivity activity) : base(activity)
		{
		}

		public override int ItemCount => 1;

		public override Fragment CreateFragment(int position)
		{
			return new Fragment();
		}
	}

	sealed class StatelessTabConfigurationStrategy : Java.Lang.Object, TabLayoutMediator.ITabConfigurationStrategy
	{
		readonly int _cycle;

		public StatelessTabConfigurationStrategy(int cycle)
		{
			_cycle = cycle;
		}

		public void OnConfigureTab(TabLayout.Tab tab, int position)
		{
			tab.SetText($"Control {_cycle + 1:0000}");
		}
	}

	sealed class StatelessPageChangeCallback : ViewPager2.OnPageChangeCallback
	{
		public override void OnPageSelected(int position)
		{
			base.OnPageSelected(position);
		}
	}

	internal sealed class FakeShellContext : IShellContext
	{
		public FakeShellContext(Context androidContext, Shell shell)
		{
			AndroidContext = androidContext;
			Shell = shell;
		}

		public Context AndroidContext { get; }
		public DrawerLayout CurrentDrawerLayout => throw new NotSupportedException();
		public Shell Shell { get; }

		public IShellObservableFragment CreateFragmentForPage(Page page) => throw new NotSupportedException();
		public IShellFlyoutContentRenderer CreateShellFlyoutContentRenderer() => throw new NotSupportedException();
		public IShellItemRenderer CreateShellItemRenderer(ShellItem shellItem) => throw new NotSupportedException();
		public IShellSectionRenderer CreateShellSectionRenderer(ShellSection shellSection) => throw new NotSupportedException();
		public IShellToolbarTracker CreateTrackerForToolbar(AToolbar toolbar) => throw new NotSupportedException();
		public IShellToolbarAppearanceTracker CreateToolbarAppearanceTracker() => throw new NotSupportedException();
		public IShellTabLayoutAppearanceTracker CreateTabLayoutAppearanceTracker(ShellSection shellSection) => throw new NotSupportedException();
		public IShellBottomNavViewAppearanceTracker CreateBottomNavViewAppearanceTracker(ShellItem shellItem) => throw new NotSupportedException();
	}

	sealed class PayloadShellHandler : IViewHandler
	{
		IView? _virtualView;

		public PayloadShellHandler(IMauiContext mauiContext, IView virtualView)
		{
			MauiContext = mauiContext;
			_virtualView = virtualView;
		}

		public bool HasContainer { get; set; }
		public object? ContainerView => null;
		public object? PlatformView => null;
		public IView? VirtualView => _virtualView;
		IElement? IElementHandler.VirtualView => _virtualView;
		public IMauiContext? MauiContext { get; private set; }

		public void SetMauiContext(IMauiContext mauiContext)
		{
			MauiContext = mauiContext;
		}

		public void SetVirtualView(IElement view)
		{
			_virtualView = (IView)view;
		}

		public void UpdateValue(string property)
		{
		}

		public void Invoke(string command, object? args = null)
		{
		}

		public void DisconnectHandler()
		{
			_virtualView = null;
		}

		public Size GetDesiredSize(double widthConstraint, double heightConstraint)
		{
			return Size.Zero;
		}

		public void PlatformArrange(Rect frame)
		{
		}
	}

	internal sealed class PayloadService
	{
		public PayloadService(int cycle, int payloadBytes)
		{
			Cycle = cycle;
			Payload = new byte[payloadBytes];
			Payload[0] = (byte)(cycle % byte.MaxValue);
			Payload[^1] = (byte)((cycle + 97) % byte.MaxValue);
		}

		public int Cycle { get; }
		public byte[] Payload { get; }
	}

	sealed class PayloadServiceProvider : IServiceProvider
	{
		readonly IServiceProvider _inner;
		readonly PayloadService _payloadService;

		public PayloadServiceProvider(IServiceProvider inner, PayloadService payloadService)
		{
			_inner = inner;
			_payloadService = payloadService;
		}

		public object? GetService(Type serviceType)
		{
			if (serviceType == typeof(PayloadService))
				return _payloadService;

			return _inner.GetService(serviceType);
		}
	}

	internal sealed record TrackedCycle(
		int Cycle,
		WeakReference<ViewPager2> ViewPager,
		WeakReference<TabLayout> TabLayout,
		WeakReference<ShellSectionRenderer> Renderer,
		WeakReference<Shell> Shell,
		WeakReference<IElementHandler> ShellHandler,
		WeakReference<FakeShellContext> ShellContext,
		WeakReference<IMauiContext> MauiContext,
		WeakReference<IServiceProvider> PayloadProvider,
		WeakReference<PayloadService> PayloadService,
		WeakReference<byte[]> PayloadBuffer,
		bool UsedCurrentRendererRegistrations)
	{
		public static TrackedCycle Create(
			int cycle,
			ViewPager2 viewPager,
			TabLayout tabLayout,
			ShellSectionRenderer renderer,
			Shell shell,
			IElementHandler shellHandler,
			FakeShellContext shellContext,
			IMauiContext mauiContext,
			IServiceProvider payloadProvider,
			PayloadService payloadService,
			byte[] payloadBuffer,
			bool usedCurrentRendererRegistrations)
		{
			return new TrackedCycle(
				cycle,
				new WeakReference<ViewPager2>(viewPager),
				new WeakReference<TabLayout>(tabLayout),
				new WeakReference<ShellSectionRenderer>(renderer),
				new WeakReference<Shell>(shell),
				new WeakReference<IElementHandler>(shellHandler),
				new WeakReference<FakeShellContext>(shellContext),
				new WeakReference<IMauiContext>(mauiContext),
				new WeakReference<IServiceProvider>(payloadProvider),
				new WeakReference<PayloadService>(payloadService),
				new WeakReference<byte[]>(payloadBuffer),
				usedCurrentRendererRegistrations);
		}
	}

	internal sealed record ScenarioResult(
		string Name,
		int TrackedCycles,
		int AliveViewPagers,
		int AliveTabLayouts,
		int AliveRenderers,
		int AliveShells,
		int AliveShellHandlers,
		int AliveShellContexts,
		int AliveMauiContexts,
		int AlivePayloadProviders,
		int AlivePayloadServices,
		int AlivePayloadBuffers,
		long RetainedPayloadBytes,
		int CyclesUsingCurrentRegistrations)
	{
		internal static ScenarioResult From(string name, IReadOnlyList<TrackedCycle> tracked)
		{
			var aliveViewPagers = 0;
			var aliveTabLayouts = 0;
			var aliveRenderers = 0;
			var aliveShells = 0;
			var aliveShellHandlers = 0;
			var aliveShellContexts = 0;
			var aliveMauiContexts = 0;
			var alivePayloadProviders = 0;
			var alivePayloadServices = 0;
			var alivePayloadBuffers = 0;
			var cyclesUsingCurrentRegistrations = 0;

			foreach (var cycle in tracked)
			{
				if (cycle.ViewPager.TryGetTarget(out _))
					aliveViewPagers++;

				if (cycle.TabLayout.TryGetTarget(out _))
					aliveTabLayouts++;

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

				if (cycle.PayloadProvider.TryGetTarget(out _))
					alivePayloadProviders++;

				if (cycle.PayloadService.TryGetTarget(out _))
					alivePayloadServices++;

				if (cycle.PayloadBuffer.TryGetTarget(out _))
					alivePayloadBuffers++;

				if (cycle.UsedCurrentRendererRegistrations)
					cyclesUsingCurrentRegistrations++;
			}

			return new ScenarioResult(
				name,
				tracked.Count,
				aliveViewPagers,
				aliveTabLayouts,
				aliveRenderers,
				aliveShells,
				aliveShellHandlers,
				aliveShellContexts,
				aliveMauiContexts,
				alivePayloadProviders,
				alivePayloadServices,
				alivePayloadBuffers,
				(long)alivePayloadBuffers * PayloadBytesPerCycle,
				cyclesUsingCurrentRegistrations);
		}
	}
}

internal sealed record ReproReport(
	int Cycles,
	int PayloadBytesPerCycle,
	long BaselineManagedBytes,
	long FinalManagedBytes,
	ReproSession.ScenarioResult Control,
	ReproSession.ScenarioResult Current)
{
	public bool LeakProved =>
		Control.AliveViewPagers == Cycles &&
		Current.AliveViewPagers == Cycles &&
		Control.AliveTabLayouts == Cycles &&
		Current.AliveTabLayouts == Cycles &&
		Control.CyclesUsingCurrentRegistrations == 0 &&
		Current.CyclesUsingCurrentRegistrations == Cycles &&
		Control.AliveRenderers == 0 &&
		Control.AliveMauiContexts == 0 &&
		Control.AlivePayloadBuffers == 0 &&
		Current.AliveRenderers == Cycles &&
		Current.AliveShells == Cycles &&
		Current.AliveShellContexts == Cycles &&
		Current.AliveMauiContexts == Cycles &&
		Current.AlivePayloadBuffers == Cycles &&
		Current.RetainedPayloadBytes >= 50L * 1024 * 1024;

	public string ToText()
	{
		var heapDeltaMiB = (FinalManagedBytes - BaselineManagedBytes) / 1024d / 1024d;
		var controlMiB = Control.RetainedPayloadBytes / 1024d / 1024d;
		var currentMiB = Current.RetainedPayloadBytes / 1024d / 1024d;

		return string.Join(Environment.NewLine,
			"AndroidShellSectionViewPagerListenerRetentionRepro",
			$"Cycles per scenario: {Cycles}",
			$"Payload bytes per cycle: {PayloadBytesPerCycle:N0}",
			"Source path exercised: PlatformInterop.createShellViewPager registers ShellSectionRenderer as TabLayoutMediator strategy and ViewPager2 page callback",
			"Both scenarios retain native ViewPager2 and TabLayout peers, release the adapter, and avoid Shell title/icon payloads",
			"Control uses stateless strategy/callback objects; current uses ShellSectionRenderer and its private ViewPagerPageChanged callback",
			$"Baseline managed heap: {BaselineManagedBytes:N0} bytes",
			$"Final managed heap: {FinalManagedBytes:N0} bytes",
			$"Managed heap delta: {heapDeltaMiB:N1} MiB",
			$"Leak proved: {LeakProved}",
			string.Empty,
			Format(Control),
			string.Empty,
			Format(Current),
			string.Empty,
			$"Control retained scoped-service payload: {controlMiB:N1} MiB",
			$"Current retained scoped-service payload: {currentMiB:N1} MiB",
			$"RESULT: {(LeakProved ? "PROVEN" : "NOT PROVEN")}");
	}

	static string Format(ReproSession.ScenarioResult result)
	{
		return string.Join(Environment.NewLine,
			$"Run: {result.Name}",
			$"  tracked cycles: {result.TrackedCycles}",
			$"  current renderer registrations: {result.CyclesUsingCurrentRegistrations}/{result.TrackedCycles}",
			$"  alive native ViewPager2 peers: {result.AliveViewPagers}/{result.TrackedCycles}",
			$"  alive native TabLayout peers: {result.AliveTabLayouts}/{result.TrackedCycles}",
			$"  alive ShellSectionRenderers: {result.AliveRenderers}/{result.TrackedCycles}",
			$"  alive Shells: {result.AliveShells}/{result.TrackedCycles}",
			$"  alive Shell handlers: {result.AliveShellHandlers}/{result.TrackedCycles}",
			$"  alive fake Shell contexts: {result.AliveShellContexts}/{result.TrackedCycles}",
			$"  alive MauiContexts: {result.AliveMauiContexts}/{result.TrackedCycles}",
			$"  alive payload providers: {result.AlivePayloadProviders}/{result.TrackedCycles}",
			$"  alive payload services: {result.AlivePayloadServices}/{result.TrackedCycles}",
			$"  alive payload buffers: {result.AlivePayloadBuffers}/{result.TrackedCycles}",
			$"  retained scoped-service payload bytes: {result.RetainedPayloadBytes:N0}");
	}
}
