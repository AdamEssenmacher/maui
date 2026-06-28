#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Android.App;
using Android.Content;
using Android.Views;
using AndroidX.AppCompat.App;
using AndroidX.Fragment.App;
using Microsoft.Maui;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Controls.Handlers;
using Microsoft.Maui.Controls.PlatformConfiguration;
using Microsoft.Maui.Controls.PlatformConfiguration.AndroidSpecific;
using Microsoft.Maui.Graphics;
using Microsoft.Maui.Platform;
using AndroidXFragmentManager = AndroidX.Fragment.App.FragmentManager;
using ControlsTabbedPage = Microsoft.Maui.Controls.TabbedPage;

namespace AndroidTabbedPageRootViewChangedRetentionRepro;

public sealed record RunStats(
	string Name,
	int Attempts,
	int AliveRootManagers,
	int RootViewChangedSubscribers,
	int AliveTabbedPageManagers,
	int ManagersWithPreviousPage,
	int AliveTabbedPages,
	int AliveCurrentPages,
	int AlivePayloads,
	int AlivePayloadBuffers,
	long HeapBefore,
	long HeapAfter);

public sealed record ReproReport(
	int Attempts,
	int PayloadBytes,
	RunStats Control,
	RunStats Current)
{
	public bool LeakProved =>
		Control.AliveRootManagers == Attempts &&
		Control.RootViewChangedSubscribers == 0 &&
		Control.AliveTabbedPageManagers == 0 &&
		Control.ManagersWithPreviousPage == 0 &&
		Control.AliveTabbedPages == 0 &&
		Control.AliveCurrentPages == 0 &&
		Control.AlivePayloads == 0 &&
		Control.AlivePayloadBuffers == 0 &&
		Current.AliveRootManagers == Attempts &&
		Current.RootViewChangedSubscribers == Attempts &&
		Current.AliveTabbedPageManagers == Attempts &&
		Current.ManagersWithPreviousPage == Attempts &&
		Current.AliveCurrentPages == Attempts &&
		Current.AlivePayloads == Attempts &&
		Current.AlivePayloadBuffers == Attempts;

	public string ToText()
	{
		return string.Join(Environment.NewLine,
			"AndroidTabbedPageRootViewChangedRetentionRepro",
			$"Transient TabbedPage workflows: {Attempts}",
			$"Payload per current tab page: {PayloadBytes / 1024 / 1024} MiB",
			$"Leak proved: {LeakProved}",
			string.Empty,
			Format(Control),
			string.Empty,
			Format(Current));
	}

	string Format(RunStats stats)
	{
		var retainedPayloadBytes = (long)stats.AlivePayloadBuffers * PayloadBytes;
		var totalPayloadBytes = (long)stats.Attempts * PayloadBytes;

		return string.Join(Environment.NewLine,
			$"Run: {stats.Name}",
			$"  root managers alive after full GC: {stats.AliveRootManagers}/{stats.Attempts}",
			$"  RootViewChanged subscribers still registered: {stats.RootViewChangedSubscribers}/{stats.Attempts}",
			$"  TabbedPageManager instances alive after full GC: {stats.AliveTabbedPageManagers}/{stats.Attempts}",
			$"  managers with previousPage assigned: {stats.ManagersWithPreviousPage}/{stats.Attempts}",
			$"  TabbedPage roots alive after full GC: {stats.AliveTabbedPages}/{stats.Attempts}",
			$"  current tab pages alive after full GC: {stats.AliveCurrentPages}/{stats.Attempts}",
			$"  payload view models alive after full GC: {stats.AlivePayloads}/{stats.Attempts}",
			$"  payload byte arrays alive after full GC: {stats.AlivePayloadBuffers}/{stats.Attempts}",
			$"  retained payload bytes: {FormatBytes(retainedPayloadBytes)} ({retainedPayloadBytes * 100.0 / totalPayloadBytes:0.0}%)",
			$"  managed heap before: {FormatBytes(stats.HeapBefore)}",
			$"  managed heap after: {FormatBytes(stats.HeapAfter)}",
			$"  managed heap delta: {FormatBytes(stats.HeapAfter - stats.HeapBefore)}");
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
	const int Attempts = 80;
	const int PayloadBytes = 1024 * 1024;

	static readonly List<NavigationRootManager> RetainedRootManagers = new();

	static readonly FieldInfo RootViewChangedField =
		typeof(NavigationRootManager).GetField("RootViewChanged", BindingFlags.Instance | BindingFlags.NonPublic)
		?? throw new MissingFieldException(typeof(NavigationRootManager).FullName, "RootViewChanged");

	static readonly FieldInfo PreviousPageField =
		typeof(TabbedPageManager).GetField("previousPage", BindingFlags.Instance | BindingFlags.NonPublic)
		?? throw new MissingFieldException(typeof(TabbedPageManager).FullName, "previousPage");

	public static async Task<ReproReport> RunAsync(AppCompatActivity activity)
	{
		await Task.Yield();

		RetainedRootManagers.Clear();

		var control = await RunScenarioAsync(
			activity,
			"control: clear pending RootViewChanged delegate after disconnect",
			clearRootViewChangedAfterDisconnect: true);

		var current = await RunScenarioAsync(
			activity,
			"current: SetElement(null) leaves RootViewChanged delegate and previousPage",
			clearRootViewChangedAfterDisconnect: false);

		GC.KeepAlive(RetainedRootManagers);
		return new ReproReport(Attempts, PayloadBytes, control, current);
	}

	static async Task<RunStats> RunScenarioAsync(
		AppCompatActivity activity,
		string name,
		bool clearRootViewChangedAfterDisconnect)
	{
		ForceFullGc();
		var heapBefore = GC.GetTotalMemory(forceFullCollection: true);

		var retainedStartIndex = RetainedRootManagers.Count;
		var rootRefs = new List<WeakReference<NavigationRootManager>>(Attempts);
		var managerRefs = new List<WeakReference<TabbedPageManager>>(Attempts);
		var tabbedPageRefs = new List<WeakReference<ControlsTabbedPage>>(Attempts);
		var currentPageRefs = new List<WeakReference<PayloadContentPage>>(Attempts);
		var payloadRefs = new List<WeakReference<PayloadViewModel>>(Attempts);
		var bufferRefs = new List<WeakReference<byte[]>>(Attempts);

		for (var i = 0; i < Attempts; i++)
		{
			CreateDisconnectedTabbedPageBeforeRootViewReady(
				activity,
				clearRootViewChangedAfterDisconnect,
				rootRefs,
				managerRefs,
				tabbedPageRefs,
				currentPageRefs,
				payloadRefs,
				bufferRefs,
				i);

			if (i % 10 == 0)
				await Task.Yield();
		}

		ForceFullGc();
		await Task.Delay(250);
		ForceFullGc();
		var heapAfter = GC.GetTotalMemory(forceFullCollection: true);

		var roots = RetainedRootManagers.Skip(retainedStartIndex).ToArray();
		var subscriberCount = roots.Sum(GetRootViewChangedSubscriberCount);
		var managersWithPreviousPage = managerRefs.Count(static managerRef =>
			managerRef.TryGetTarget(out var manager) &&
			PreviousPageField.GetValue(manager) is not null);

		GC.KeepAlive(RetainedRootManagers);

		return new RunStats(
			name,
			Attempts,
			CountAlive(rootRefs),
			subscriberCount,
			CountAlive(managerRefs),
			managersWithPreviousPage,
			CountAlive(tabbedPageRefs),
			CountAlive(currentPageRefs),
			CountAlive(payloadRefs),
			CountAlive(bufferRefs),
			heapBefore,
			heapAfter);
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	static void CreateDisconnectedTabbedPageBeforeRootViewReady(
		AppCompatActivity activity,
		bool clearRootViewChangedAfterDisconnect,
		List<WeakReference<NavigationRootManager>> rootRefs,
		List<WeakReference<TabbedPageManager>> managerRefs,
		List<WeakReference<ControlsTabbedPage>> tabbedPageRefs,
		List<WeakReference<PayloadContentPage>> currentPageRefs,
		List<WeakReference<PayloadViewModel>> payloadRefs,
		List<WeakReference<byte[]>> bufferRefs,
		int index)
	{
		var services = new ReproServiceProvider(activity);
		var mauiContext = new MauiContext(services, activity);
		var rootManager = new NavigationRootManager(mauiContext);
		services.RootManager = rootManager;

		var manager = new TabbedPageManager(mauiContext);
		var payload = new PayloadViewModel(index, PayloadBytes);
		var currentPage = new PayloadContentPage(index, payload);
		var tabbedPage = new ControlsTabbedPage
		{
			Title = $"Transient workspace {index + 1:000}",
			TabbedPageManager = manager
		};

		tabbedPage.On<Microsoft.Maui.Controls.PlatformConfiguration.Android>().SetToolbarPlacement(ToolbarPlacement.Bottom);
		tabbedPage.Children.Add(currentPage);
		tabbedPage.CurrentPage = currentPage;

		var handler = new FakeElementHandler(mauiContext);
		handler.SetVirtualView(tabbedPage);
		tabbedPage.Handler = handler;

		manager.SetElement(tabbedPage);

		// This is the disconnect path used by TabbedPage.Android.cs. It clears Element,
		// but it does not unsubscribe RootViewChanged or clear previousPage.
		manager.SetElement(null!);
		tabbedPage.Handler = null!;

		if (clearRootViewChangedAfterDisconnect)
			RootViewChangedField.SetValue(rootManager, null);

		RetainedRootManagers.Add(rootManager);
		rootRefs.Add(new WeakReference<NavigationRootManager>(rootManager));
		managerRefs.Add(new WeakReference<TabbedPageManager>(manager));
		tabbedPageRefs.Add(new WeakReference<ControlsTabbedPage>(tabbedPage));
		currentPageRefs.Add(new WeakReference<PayloadContentPage>(currentPage));
		payloadRefs.Add(new WeakReference<PayloadViewModel>(payload));
		bufferRefs.Add(new WeakReference<byte[]>(payload.Bytes));

		rootManager = null!;
		mauiContext = null!;
		manager = null!;
		tabbedPage = null!;
		currentPage = null!;
		payload = null!;
		handler = null!;
		services = null!;
	}

	static int GetRootViewChangedSubscriberCount(NavigationRootManager rootManager)
	{
		var del = RootViewChangedField.GetValue(rootManager) as MulticastDelegate;
		return del?.GetInvocationList().Length ?? 0;
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
		for (var i = 0; i < 6; i++)
		{
			GC.Collect();
			GC.WaitForPendingFinalizers();
			GC.Collect();
			Task.Delay(25).Wait();
		}
	}

	sealed class ReproServiceProvider : IServiceProvider
	{
		readonly AppCompatActivity _activity;

		public ReproServiceProvider(AppCompatActivity activity)
		{
			_activity = activity;
		}

		public NavigationRootManager? RootManager { get; set; }

		public object? GetService(Type serviceType)
		{
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

	sealed class FakeElementHandler : IViewHandler
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
			VirtualView = null;
			MauiContext = null;
		}

		public Size GetDesiredSize(double widthConstraint, double heightConstraint) => Size.Zero;

		public void PlatformArrange(Rect frame)
		{
		}
	}

	sealed class PayloadContentPage : ContentPage
	{
		public PayloadContentPage(int index, PayloadViewModel payload)
		{
			Title = $"Orders {index + 1:000}";
			BindingContext = payload;
			Content = new Label
			{
				Text = payload.Title,
				AutomationId = $"payload-label-{index + 1:000}"
			};
		}
	}

	sealed class PayloadViewModel
	{
		public PayloadViewModel(int index, int payloadBytes)
		{
			Title = $"Customer order workspace {index + 1:000}";
			Bytes = new byte[payloadBytes];

			for (var i = 0; i < Bytes.Length; i += 4096)
				Bytes[i] = (byte)(index + i);

			OpenInvoices = Enumerable.Range(1, 40)
				.Select(invoice => $"INV-{index + 1:000}-{invoice:000}")
				.ToArray();
		}

		public string Title { get; }

		public byte[] Bytes { get; }

		public IReadOnlyList<string> OpenInvoices { get; }
	}
}
