#nullable enable

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Android.App;
using Android.Content;
using Android.Views;
using AndroidX.AppCompat.App;
using AndroidX.Fragment.App;
using AndroidX.ViewPager2.Widget;
using Google.Android.Material.BottomNavigation;
using Google.Android.Material.Tabs;
using Microsoft.Maui;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Controls.Handlers;
using Microsoft.Maui.Controls.PlatformConfiguration;
using Microsoft.Maui.Controls.PlatformConfiguration.AndroidSpecific;
using Microsoft.Maui.Graphics;
using Microsoft.Maui.Platform;
using AndroidXFragmentManager = AndroidX.Fragment.App.FragmentManager;
using ControlsTabbedPage = Microsoft.Maui.Controls.TabbedPage;

namespace AndroidTabbedPageManagerItemsSourceResetSubscriptionRetentionRepro;

internal static class ReproSession
{
	const int Cycles = 80;
	const int ChildrenPerTabbedPage = 3;
	const int PayloadKiBPerContext = 1024;

	const long PayloadBytesPerContext = PayloadKiBPerContext * 1024L;

	static readonly List<IReadOnlyList<Page>> RetainedChildPages = new();

	static readonly FieldInfo RootViewChangedField =
		typeof(NavigationRootManager).GetField("RootViewChanged", BindingFlags.Instance | BindingFlags.NonPublic)
		?? throw new MissingFieldException(typeof(NavigationRootManager).FullName, "RootViewChanged");

	static readonly FieldInfo PreviousPageField =
		typeof(TabbedPageManager).GetField("previousPage", BindingFlags.Instance | BindingFlags.NonPublic)
		?? throw new MissingFieldException(typeof(TabbedPageManager).FullName, "previousPage");

	static readonly FieldInfo ListenersField =
		typeof(TabbedPageManager).GetField("_listeners", BindingFlags.Instance | BindingFlags.NonPublic)
		?? throw new MissingFieldException(typeof(TabbedPageManager).FullName, "_listeners");

	static readonly FieldInfo TabLayoutMediatorField =
		typeof(TabbedPageManager).GetField("_tabLayoutMediator", BindingFlags.Instance | BindingFlags.NonPublic)
		?? throw new MissingFieldException(typeof(TabbedPageManager).FullName, "_tabLayoutMediator");

	static readonly FieldInfo TabbedPageManagerField =
		typeof(ControlsTabbedPage).GetField("_tabbedPageManager", BindingFlags.Instance | BindingFlags.NonPublic)
		?? throw new MissingFieldException(typeof(ControlsTabbedPage).FullName, "_tabbedPageManager");

	public static async Task<ReproReport> RunAsync(AppCompatActivity activity)
	{
		RetainedChildPages.Clear();

		ForceFullGc();
		var baselineBytes = GC.GetTotalMemory(true);

		var control = await RunScenarioAsync(
			activity,
			"control: remove generated child subscriptions before ItemsSource.Clear()",
			removeGeneratedChildSubscriptions: true);

		var current = await RunScenarioAsync(
			activity,
			"current: ItemsSource.Clear() leaves generated child pages subscribed",
			removeGeneratedChildSubscriptions: false);

		ForceFullGc();
		var finalBytes = GC.GetTotalMemory(true);

		GC.KeepAlive(RetainedChildPages);

		return new ReproReport(
			Cycles,
			ChildrenPerTabbedPage,
			PayloadKiBPerContext,
			baselineBytes,
			finalBytes,
			control,
			current);
	}

	static async Task<ScenarioResult> RunScenarioAsync(
		AppCompatActivity activity,
		string name,
		bool removeGeneratedChildSubscriptions)
	{
		var retainedChildren = new List<Page>(Cycles * ChildrenPerTabbedPage);
		var tracked = new List<TrackedCycle>(Cycles);

		for (var i = 0; i < Cycles; i++)
		{
			CreateDisconnectedTabbedPageManagerCycle(
				activity,
				i,
				retainedChildren,
				tracked,
				removeGeneratedChildSubscriptions);

			if (i % 8 == 0)
				await Task.Yield();
		}

		RetainedChildPages.Add(retainedChildren);

		ForceFullGc();

		return ScenarioResult.From(name, retainedChildren, tracked);
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	static void CreateDisconnectedTabbedPageManagerCycle(
		AppCompatActivity activity,
		int cycle,
		List<Page> retainedChildren,
		List<TrackedCycle> tracked,
		bool removeGeneratedChildSubscriptions)
	{
		var services = new ReproServiceProvider(activity, cycle, checked((int)PayloadBytesPerContext));
		var mauiContext = new MauiContext(services, activity);
		var rootManager = new NavigationRootManager(mauiContext);
		services.RootManager = rootManager;

		var manager = new TabbedPageManager(mauiContext);
		var generatedPages = new List<Page>(ChildrenPerTabbedPage);
		var items = new ObservableCollection<PayloadTabItem>();
		var tabbedPage = new ControlsTabbedPage
		{
			Title = $"Regional operations tabs {cycle + 1:000}",
			AutomationId = $"android-tabbed-items-source-reset-{cycle + 1:000}",
			ItemTemplate = new DataTemplate(() =>
			{
				var childIndex = generatedPages.Count;
				var page = new PayloadChildPage(cycle, childIndex);
				generatedPages.Add(page);
				return page;
			}),
			TabbedPageManager = manager
		};

		tabbedPage.On<Microsoft.Maui.Controls.PlatformConfiguration.Android>()
			.SetToolbarPlacement(ToolbarPlacement.Bottom);

		for (var i = 0; i < ChildrenPerTabbedPage; i++)
			items.Add(new PayloadTabItem(cycle, i));

		tabbedPage.ItemsSource = items;

		if (generatedPages.Count != ChildrenPerTabbedPage || tabbedPage.Children.Count != ChildrenPerTabbedPage)
			throw new InvalidOperationException($"ItemsSource did not generate {ChildrenPerTabbedPage} child pages.");

		var handler = new FakeElementHandler(mauiContext);
		handler.SetVirtualView(tabbedPage);
		tabbedPage.Handler = handler;

		manager.SetElement(tabbedPage);

		var childPages = generatedPages.ToArray();
		var payload = services.Payload;

		if (payload.Buffer.Length != PayloadBytesPerContext || payload.Touch() == 0)
			throw new InvalidOperationException("The synthetic context payload was not initialized.");

		tracked.Add(TrackedCycle.Create(
			cycle,
			manager,
			tabbedPage,
			childPages,
			mauiContext,
			services,
			payload,
			payload.Buffer));

		if (removeGeneratedChildSubscriptions)
		{
			foreach (var childPage in childPages)
				TeardownPage(manager, childPage);
		}

		items.Clear();

		if (tabbedPage.Children.Count != 0)
			throw new InvalidOperationException("ItemsSource.Clear() did not remove the generated child pages.");

		// Neutralize the already-cataloged C141 logical-child reset root so this
		// repro isolates TabbedPageManager's generated-page subscriptions.
		tabbedPage.ClearLogicalChildren();

		manager.SetElement(null!);
		tabbedPage.Handler = null!;
		handler.DisconnectHandler();
		NeutralizeKnownManagerRoots(rootManager, manager, tabbedPage);

		retainedChildren.AddRange(childPages);

		rootManager = null!;
		mauiContext = null!;
		manager = null!;
		tabbedPage = null!;
		handler = null!;
		services = null!;
		generatedPages = null!;
		items = null!;
	}

	static void NeutralizeKnownManagerRoots(NavigationRootManager rootManager, TabbedPageManager manager, ControlsTabbedPage tabbedPage)
	{
		RootViewChangedField.SetValue(rootManager, null);
		PreviousPageField.SetValue(manager, null);
		TabbedPageManagerField.SetValue(tabbedPage, null);

		if (TabLayoutMediatorField.GetValue(manager) is TabLayoutMediator mediator)
		{
			mediator.Detach();
			TabLayoutMediatorField.SetValue(manager, null);
		}

		if (ListenersField.GetValue(manager) is ViewPager2.OnPageChangeCallback pageChangeCallback)
			manager.ViewPager.UnregisterOnPageChangeCallback(pageChangeCallback);

		manager.TabLayout?.ClearOnTabSelectedListeners();
		manager.BottomNavigationView?.SetOnItemSelectedListener(null);
		manager.ViewPager.Adapter = null;
	}

	static int CountGeneratedChildSubscriptions(IReadOnlyList<Page> pages)
	{
		var count = 0;

		foreach (var page in pages)
		{
			var handler = PropertyChangedField(page);
			if (handler is null)
				continue;

			foreach (var subscriber in handler.GetInvocationList())
			{
				if (subscriber.Target is TabbedPageManager)
					count++;
			}
		}

		return count;
	}

	static int CountAlive<T>(IEnumerable<WeakReference<T>> references)
		where T : class
	{
		var count = 0;
		foreach (var reference in references)
		{
			if (reference.TryGetTarget(out _))
				count++;
		}

		return count;
	}

	static void ForceFullGc()
	{
		for (var i = 0; i < 5; i++)
		{
			GC.Collect();
			GC.WaitForPendingFinalizers();
			GC.Collect();
			Java.Lang.JavaSystem.Gc();
			Thread.Sleep(100);
		}
	}

	[UnsafeAccessor(UnsafeAccessorKind.Method, Name = "TeardownPage")]
	static extern void TeardownPage(TabbedPageManager manager, Page page);

	[UnsafeAccessor(UnsafeAccessorKind.Field, Name = "PropertyChanged")]
	static extern ref PropertyChangedEventHandler? PropertyChangedField(BindableObject bindable);

	[UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_context")]
	static extern ref IMauiContext ContextField(TabbedPageManager manager);

	internal sealed class ReproServiceProvider : IServiceProvider
	{
		readonly AppCompatActivity _activity;

		public ReproServiceProvider(AppCompatActivity activity, int cycle, int payloadBytes)
		{
			_activity = activity;
			Payload = new PayloadService(cycle, payloadBytes);
		}

		public NavigationRootManager? RootManager { get; set; }

		public PayloadService Payload { get; }

		public object? GetService(Type serviceType)
		{
			if (serviceType == typeof(PayloadService))
				return Payload;
			if (serviceType == typeof(NavigationRootManager))
				return RootManager;
			if (serviceType == typeof(Activity))
				return _activity;
			if (serviceType == typeof(Context))
				return _activity;
			if (serviceType == typeof(LayoutInflater))
				return LayoutInflater.From(_activity);
			if (serviceType == typeof(AndroidXFragmentManager))
				return _activity.SupportFragmentManager;

			return null;
		}
	}

	internal sealed class FakeElementHandler : IViewHandler
	{
		public FakeElementHandler(IMauiContext mauiContext)
		{
			MauiContext = mauiContext;
		}

		public object? PlatformView => null;

		public IView? VirtualView { get; private set; }

		IElement? IElementHandler.VirtualView => VirtualView;

		public IMauiContext? MauiContext { get; private set; }

		public void SetMauiContext(IMauiContext mauiContext)
		{
			MauiContext = mauiContext;
		}

		public bool HasContainer { get; set; }

		public object? ContainerView => null;

		public void SetVirtualView(IElement view)
		{
			VirtualView = (IView)view;
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

		public Size GetDesiredSize(double widthConstraint, double heightConstraint) => Size.Zero;

		public void PlatformArrange(Microsoft.Maui.Graphics.Rect frame)
		{
		}
	}

	internal sealed record TrackedCycle(
		int Cycle,
		WeakReference<TabbedPageManager> Manager,
		WeakReference<ControlsTabbedPage> TabbedPage,
		IReadOnlyList<WeakReference<Page>> ChildPages,
		WeakReference<IMauiContext> Context,
		WeakReference<ReproServiceProvider> Provider,
		WeakReference<PayloadService> Payload,
		WeakReference<byte[]> PayloadBuffer)
	{
		public static TrackedCycle Create(
			int cycle,
			TabbedPageManager manager,
			ControlsTabbedPage tabbedPage,
			IReadOnlyList<Page> childPages,
			IMauiContext context,
			ReproServiceProvider provider,
			PayloadService payload,
			byte[] payloadBuffer)
		{
			return new TrackedCycle(
				cycle,
				new WeakReference<TabbedPageManager>(manager),
				new WeakReference<ControlsTabbedPage>(tabbedPage),
				childPages.Select(static page => new WeakReference<Page>(page)).ToArray(),
				new WeakReference<IMauiContext>(context),
				new WeakReference<ReproServiceProvider>(provider),
				new WeakReference<PayloadService>(payload),
				new WeakReference<byte[]>(payloadBuffer));
		}
	}

	internal sealed record ScenarioResult(
		string Name,
		int TrackedCycles,
		int RetainedChildPages,
		int ChildPageSubscriptionsToTabbedPageManager,
		int AliveTabbedPageManagers,
		int ManagersWithMauiContext,
		int ManagersResolvingPayloads,
		long EstimatedContextPayloadBytes,
		int AliveTabbedPages,
		int AliveChildPages,
		int AliveContexts,
		int AliveProviders,
		int AlivePayloads,
		int AlivePayloadBuffers)
	{
		internal static ScenarioResult From(
			string name,
			IReadOnlyList<Page> retainedChildPages,
			IReadOnlyList<TrackedCycle> tracked)
		{
			var aliveManagers = 0;
			var managersWithMauiContext = 0;
			var managersResolvingPayloads = 0;
			long estimatedContextPayloadBytes = 0;
			var aliveTabbedPages = 0;
			var aliveChildPages = 0;
			var aliveContexts = 0;
			var aliveProviders = 0;
			var alivePayloads = 0;
			var alivePayloadBuffers = 0;

			foreach (var cycle in tracked)
			{
				if (cycle.Manager.TryGetTarget(out var manager))
				{
					aliveManagers++;

					var managerContext = ContextField(manager);
					if (managerContext is not null)
					{
						managersWithMauiContext++;

						if (managerContext.Services.GetService(typeof(PayloadService)) is PayloadService payload)
						{
							managersResolvingPayloads++;
							estimatedContextPayloadBytes += Math.Min(payload.Buffer.Length, PayloadBytesPerContext);
						}
					}
				}

				if (cycle.TabbedPage.TryGetTarget(out _))
					aliveTabbedPages++;

				foreach (var childPage in cycle.ChildPages)
				{
					if (childPage.TryGetTarget(out _))
						aliveChildPages++;
				}

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
				retainedChildPages.Count,
				CountGeneratedChildSubscriptions(retainedChildPages),
				aliveManagers,
				managersWithMauiContext,
				managersResolvingPayloads,
				estimatedContextPayloadBytes,
				aliveTabbedPages,
				aliveChildPages,
				aliveContexts,
				aliveProviders,
				alivePayloads,
				alivePayloadBuffers);
		}
	}
}

sealed record PayloadTabItem(int Cycle, int Child);

sealed class PayloadChildPage : ContentPage
{
	public PayloadChildPage(int cycle, int child)
	{
		Title = $"Territory {cycle + 1:000}-{child + 1:00}";
		AutomationId = $"retained-android-tab-child-{cycle + 1:000}-{child + 1:00}";
		Content = new Label
		{
			Text = $"Open territory queue {cycle + 1:000}-{child + 1:00}",
			AutomationId = $"retained-android-tab-label-{cycle + 1:000}-{child + 1:00}"
		};
	}
}

sealed class PayloadService
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

internal sealed record ReproReport(
	int Cycles,
	int ChildrenPerTabbedPage,
	int PayloadKiBPerContext,
	long BaselineManagedBytes,
	long FinalManagedBytes,
	ReproSession.ScenarioResult Control,
	ReproSession.ScenarioResult Current)
{
	public bool LeakProved =>
		Control.RetainedChildPages == Cycles * ChildrenPerTabbedPage &&
		Control.ChildPageSubscriptionsToTabbedPageManager == 0 &&
		Control.AliveTabbedPageManagers <= 1 &&
		Control.ManagersWithMauiContext <= 1 &&
		Control.ManagersResolvingPayloads <= 1 &&
		Control.AliveContexts <= 1 &&
		Control.AliveProviders <= 1 &&
		Control.AlivePayloads <= 1 &&
		Control.AlivePayloadBuffers <= 1 &&
		Control.AliveChildPages == Cycles * ChildrenPerTabbedPage &&
		Current.RetainedChildPages == Cycles * ChildrenPerTabbedPage &&
		Current.ChildPageSubscriptionsToTabbedPageManager == Cycles * ChildrenPerTabbedPage &&
		Current.AliveTabbedPageManagers == Cycles &&
		Current.ManagersWithMauiContext == Cycles &&
		Current.ManagersResolvingPayloads == Cycles &&
		Current.AliveContexts == Cycles &&
		Current.AliveProviders == Cycles &&
		Current.AlivePayloads == Cycles &&
		Current.AlivePayloadBuffers == Cycles &&
		Current.AliveChildPages == Cycles * ChildrenPerTabbedPage &&
		Current.EstimatedContextPayloadBytes >= Cycles * PayloadKiBPerContext * 1024L * 0.95;

	public string ToText()
	{
		var currentMiB = Current.EstimatedContextPayloadBytes / 1024d / 1024d;
		var controlMiB = Control.EstimatedContextPayloadBytes / 1024d / 1024d;
		var heapDeltaMiB = (FinalManagedBytes - BaselineManagedBytes) / 1024d / 1024d;

		return string.Join(Environment.NewLine,
			"AndroidTabbedPageManagerItemsSourceResetSubscriptionRetentionRepro",
			$"Cycles: {Cycles}",
			$"Generated children per ItemsSource reset retained in both runs: {ChildrenPerTabbedPage}",
			$"Payload per MauiContext: {PayloadKiBPerContext} KiB",
			$"Expected current retained context payload: {FormatBytes(Cycles * PayloadKiBPerContext * 1024L)}",
			$"Source path mirrored: MultiPage<T>.ItemsSource reset -> InternalChildren.Clear() Reset -> TabbedPageManager.Reset() without old generated pages",
			$"Managed root neutralization: C141 stale logical children, TabbedPage._tabbedPageManager, RootViewChanged, previousPage, TabLayoutMediator, tab listeners, bottom item listener, ViewPager callback, and ViewPager adapter cleared in both runs",
			$"Baseline managed heap: {BaselineManagedBytes:N0} bytes",
			$"Final managed heap: {FinalManagedBytes:N0} bytes",
			$"Managed heap delta: {heapDeltaMiB:N1} MiB",
			$"Leak proved: {LeakProved}",
			string.Empty,
			Format(Control, ChildrenPerTabbedPage),
			string.Empty,
			Format(Current, ChildrenPerTabbedPage),
			string.Empty,
			$"Control estimated retained context payload: {controlMiB:N1} MiB",
			$"Current estimated retained context payload: {currentMiB:N1} MiB",
			$"RESULT: {(LeakProved ? "PROVEN" : "NOT PROVEN")}");
	}

	static string Format(ReproSession.ScenarioResult result, int childrenPerTabbedPage)
	{
		var payloadMiB = result.EstimatedContextPayloadBytes / 1024d / 1024d;

		return string.Join(Environment.NewLine,
			$"Run: {result.Name}",
			$"  tracked cycles: {result.TrackedCycles}",
			$"  app-retained generated child pages: {result.RetainedChildPages}/{result.TrackedCycles * childrenPerTabbedPage}",
			$"  generated child subscriptions to TabbedPageManager: {result.ChildPageSubscriptionsToTabbedPageManager}/{result.TrackedCycles * childrenPerTabbedPage}",
			$"  alive TabbedPageManager instances: {result.AliveTabbedPageManagers}/{result.TrackedCycles}",
			$"  managers with retained MauiContext: {result.ManagersWithMauiContext}/{result.TrackedCycles}",
			$"  managers resolving payload service: {result.ManagersResolvingPayloads}/{result.TrackedCycles}",
			$"  estimated retained context payload bytes: {result.EstimatedContextPayloadBytes:N0}",
			$"  estimated retained context payload MiB: {payloadMiB:N1}",
			$"  alive TabbedPages: {result.AliveTabbedPages}/{result.TrackedCycles}",
			$"  alive generated child pages: {result.AliveChildPages}/{result.TrackedCycles * childrenPerTabbedPage}",
			$"  alive MauiContexts: {result.AliveContexts}/{result.TrackedCycles}",
			$"  alive payload service providers: {result.AliveProviders}/{result.TrackedCycles}",
			$"  alive payload services: {result.AlivePayloads}/{result.TrackedCycles}",
			$"  alive payload byte arrays: {result.AlivePayloadBuffers}/{result.TrackedCycles}");
	}

	static string FormatBytes(long bytes)
	{
		if (bytes >= 1024 * 1024)
			return $"{bytes / 1024d / 1024d:N1} MiB";
		if (bytes >= 1024)
			return $"{bytes / 1024d:N1} KiB";
		return $"{bytes:N0} B";
	}
}
