using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Maui.Controls.Platform.Compatibility;
using Microsoft.Maui.Hosting;
using UIKit;

namespace ShellTitleViewParentSetRetentionRepro;

public sealed class App : Application
{
	protected override Window CreateWindow(IActivationState? activationState)
	{
		var page = new ContentPage
		{
			Content = new Label
			{
				Text = "Running Shell TitleView ParentSet retention repro...",
				HorizontalOptions = LayoutOptions.Center,
				VerticalOptions = LayoutOptions.Center
			}
		};

		ShellTitleViewParentSetRetentionProbe.Schedule(nameof(CreateWindow));

		return new Window(page);
	}
}

internal static class ShellTitleViewParentSetRetentionProbe
{
	const int Cycles = 96;
	const int PayloadBytes = 1024 * 1024;
	const string EntryPath = "/tmp/shell-titleview-parentset-retention-entry.txt";
	const string ExceptionPath = "/tmp/shell-titleview-parentset-retention-exception.txt";
	static readonly BindingFlags InstanceAnyVisibility = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
	static int _scheduled;

	public static void Schedule(string source)
	{
		if (Interlocked.Exchange(ref _scheduled, 1) != 0)
			return;

		_ = Task.Run(async () =>
		{
			try
			{
				await Task.Delay(500);
				MainThread.BeginInvokeOnMainThread(() => ExecuteScheduled(source));
			}
			catch (Exception ex)
			{
				File.WriteAllText(ExceptionPath, ex.ToString());
				Environment.Exit(4);
			}
		});
	}

	static void ExecuteScheduled(string source)
	{
		try
		{
			var resultPath = GetArgumentValue("--results=");
			File.WriteAllText(EntryPath, $"Started from {source} at {DateTimeOffset.Now:O}{Environment.NewLine}");
			var exitCode = Run(resultPath);
			File.AppendAllText(EntryPath, $"Completed with exit code {exitCode} at {DateTimeOffset.Now:O}{Environment.NewLine}");
			Environment.Exit(exitCode);
		}
		catch (Exception ex)
		{
			File.WriteAllText(ExceptionPath, ex.ToString());
			Environment.Exit(3);
		}
	}

	static string? GetArgumentValue(string prefix)
	{
		foreach (var arg in Environment.GetCommandLineArgs())
		{
			if (arg.StartsWith(prefix, StringComparison.Ordinal))
				return arg[prefix.Length..];
		}

		return null;
	}

	public static int Run(string? resultPath)
	{
		var beforeBytes = GC.GetTotalMemory(forceFullCollection: true);
		var control = RunScenario(clearPendingParentSetSubscription: true);
		var current = RunScenario(clearPendingParentSetSubscription: false);
		ForceFullGc();
		var afterBytes = GC.GetTotalMemory(forceFullCollection: true);

		var proven =
			control.AliveTrackers == 0 &&
			control.AliveFontManagers == 0 &&
			control.AlivePayloads == 0 &&
			current.AliveTrackers >= Cycles * 9 / 10 &&
			current.AliveFontManagers >= Cycles * 9 / 10 &&
			current.AlivePayloads >= Cycles * 9 / 10;

		var retainedPayloadBytes = (long)current.AlivePayloads * PayloadBytes;
		var report = string.Join(Environment.NewLine,
			"Shell TitleView pending ParentSet retention repro",
			$"Cycles: {Cycles}",
			$"Payload per tracker font-manager service: {PayloadBytes:N0} bytes",
			"",
			"Control: explicit pending ParentSet unsubscribe before disposing ShellPageRendererTracker",
			$"  Trackers alive: {control.AliveTrackers}/{Cycles}",
			$"  Font managers alive: {control.AliveFontManagers}/{Cycles}",
			$"  Payload buffers alive: {control.AlivePayloads}/{Cycles}",
			"",
			"Current MAUI: ShellPageRendererTracker.Dispose() does not unsubscribe the pending TitleView.ParentSet handler",
			$"  Trackers alive: {current.AliveTrackers}/{Cycles}",
			$"  Font managers alive: {current.AliveFontManagers}/{Cycles}",
			$"  Payload buffers alive: {current.AlivePayloads}/{Cycles}",
			$"  Proven retained payload: {ToMiB(retainedPayloadBytes):N1} MiB",
			$"  Managed heap delta after both scenarios: {ToMiB(afterBytes - beforeBytes):N1} MiB",
			"",
			proven ? "RESULT: PROVEN" : "RESULT: NOT PROVEN");

		Console.WriteLine(report);

		if (!string.IsNullOrWhiteSpace(resultPath))
			File.WriteAllText(resultPath, report);

		return proven ? 0 : 2;
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	static ScenarioResult RunScenario(bool clearPendingParentSetSubscription)
	{
		var retainedTitleViews = new List<View>(Cycles);
		var trackerRefs = new List<WeakReference>(Cycles);
		var fontManagerRefs = new List<WeakReference>(Cycles);
		var payloadRefs = new List<WeakReference>(Cycles);

		for (var i = 0; i < Cycles; i++)
		{
			CreateAndDisposeTracker(clearPendingParentSetSubscription, retainedTitleViews, trackerRefs, fontManagerRefs, payloadRefs, i);
		}

		ForceFullGc();

		var result = new ScenarioResult(
			CountAlive(trackerRefs),
			CountAlive(fontManagerRefs),
			CountAlive(payloadRefs),
			retainedTitleViews);

		GC.KeepAlive(retainedTitleViews);
		return result;
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	static void CreateAndDisposeTracker(
		bool clearPendingParentSetSubscription,
		List<View> retainedTitleViews,
		List<WeakReference> trackerRefs,
		List<WeakReference> fontManagerRefs,
		List<WeakReference> payloadRefs,
		int cycle)
	{
		var titleView = new Label { Text = $"Retained deferred Shell title {cycle}" };
		var fontManager = new PayloadFontManager(PayloadBytes);
		var services = new ServiceCollection()
			.AddSingleton<IFontManager>(fontManager)
			.BuildServiceProvider();
		var mauiContext = new MauiContext(services);
		var shell = new Shell
		{
			Handler = new FakeHandler(mauiContext)
		};
		var page = new ContentPage { Title = $"Page {cycle}" };

		var toolbar = GetShellToolbar(shell);
		SetShellToolbarCurrentPage(toolbar, page);
		SetShellToolbarTitleView(toolbar, titleView);

		var viewController = new UIViewController();
		using var navigationController = new UINavigationController(viewController);
		var tracker = new ShellPageRendererTracker(new FakeShellContext(shell))
		{
			ViewController = viewController,
			Page = page
		};

		InvokeUpdateTitleView(tracker);

		if (clearPendingParentSetSubscription)
			RemovePendingParentSetSubscription(titleView, tracker);

		tracker.Dispose();
		shell.Handler = null;
		GC.KeepAlive(navigationController);

		trackerRefs.Add(new WeakReference(tracker));
		fontManagerRefs.Add(new WeakReference(fontManager));
		payloadRefs.Add(new WeakReference(fontManager.Payload));
		retainedTitleViews.Add(titleView);
	}

	static object GetShellToolbar(Shell shell)
	{
		var property = typeof(Shell).GetProperty("Toolbar", InstanceAnyVisibility)
			?? throw new MissingMemberException(nameof(Shell), "Toolbar");

		return property.GetValue(shell)
			?? throw new InvalidOperationException("Shell.Toolbar was null.");
	}

	static void SetShellToolbarCurrentPage(object toolbar, Page page)
	{
		var field = toolbar.GetType().GetField("_currentPage", InstanceAnyVisibility)
			?? throw new MissingFieldException(toolbar.GetType().FullName, "_currentPage");

		field.SetValue(toolbar, page);
	}

	static void SetShellToolbarTitleView(object toolbar, View titleView)
	{
		var property = toolbar.GetType().GetProperty("TitleView", InstanceAnyVisibility)
			?? throw new MissingMemberException(toolbar.GetType().FullName, "TitleView");

		property.SetValue(toolbar, titleView);
	}

	static void InvokeUpdateTitleView(ShellPageRendererTracker tracker)
	{
		var method = typeof(ShellPageRendererTracker).GetMethod("UpdateTitleView", InstanceAnyVisibility)
			?? throw new MissingMethodException(nameof(ShellPageRendererTracker), "UpdateTitleView");

		method.Invoke(tracker, null);
	}

	static void RemovePendingParentSetSubscription(View titleView, ShellPageRendererTracker tracker)
	{
		var eventInfo = typeof(Element).GetEvent("ParentSet", InstanceAnyVisibility)
			?? throw new MissingMemberException(nameof(Element), "ParentSet");
		var method = typeof(ShellPageRendererTracker).GetMethod("OnTitleViewParentSet", InstanceAnyVisibility)
			?? throw new MissingMethodException(nameof(ShellPageRendererTracker), "OnTitleViewParentSet");
		var removeMethod = eventInfo.GetRemoveMethod(nonPublic: true)
			?? throw new MissingMethodException(nameof(Element), "remove_ParentSet");
		var handler = Delegate.CreateDelegate(typeof(EventHandler), tracker, method);

		removeMethod.Invoke(titleView, new object[] { handler });
	}

	static int CountAlive(IEnumerable<WeakReference> references)
	{
		var count = 0;
		foreach (var reference in references)
		{
			if (reference.IsAlive)
				count++;
		}

		return count;
	}

	static void ForceFullGc()
	{
		for (var i = 0; i < 4; i++)
		{
			GC.Collect();
			GC.WaitForPendingFinalizers();
			GC.Collect();
			Thread.Sleep(50);
		}
	}

	static double ToMiB(long bytes) => bytes / 1024d / 1024d;

	sealed record ScenarioResult(
		int AliveTrackers,
		int AliveFontManagers,
		int AlivePayloads,
		List<View> RetainedTitleViews);

	sealed class PayloadFontManager : IFontManager
	{
		public PayloadFontManager(int payloadBytes)
		{
			Payload = new byte[payloadBytes];
			Payload[0] = 42;
			Payload[^1] = 42;
		}

		public byte[] Payload { get; }

		public double DefaultFontSize => 14d;

		public UIFont DefaultFont => UIFont.SystemFontOfSize(14)!;

		public UIFont GetFont(Microsoft.Maui.Font font, double defaultFontSize = 0)
		{
			var size = defaultFontSize > 0 ? defaultFontSize : DefaultFontSize;
			return UIFont.SystemFontOfSize((nfloat)size)!;
		}
	}

	sealed class FakeMauiHandlersFactory : IMauiHandlersFactory
	{
		public object? GetService(Type serviceType) => null;

		public Type? GetHandlerType(Type iview) => null;

		public IElementHandler? GetHandler(Type type) => null;

		public IElementHandler? GetHandler<T>() where T : IElement => null;

		public IMauiHandlersCollection GetCollection() => throw new NotSupportedException();
	}

	sealed class FakeHandler : IViewHandler
	{
		public FakeHandler(IMauiContext mauiContext)
		{
			MauiContext = mauiContext;
		}

		public object? PlatformView => null;

		public IView? VirtualView { get; private set; }

		IElement? IElementHandler.VirtualView => VirtualView;

		public IMauiContext? MauiContext { get; private set; }

		public bool HasContainer { get; set; }

		public object? ContainerView => null;

		public void SetMauiContext(IMauiContext mauiContext)
		{
			MauiContext = mauiContext;
		}

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

	sealed class FakeShellContext : IShellContext
	{
		public FakeShellContext(Shell shell)
		{
			Shell = shell;
		}

		public bool AllowFlyoutGesture => false;

		public IShellItemRenderer CurrentShellItemRenderer => throw new NotSupportedException();

		public Shell Shell { get; }

		public IShellPageRendererTracker CreatePageRendererTracker() => throw new NotSupportedException();

		public IShellFlyoutContentRenderer CreateShellFlyoutContentRenderer() => throw new NotSupportedException();

		public IShellSectionRenderer CreateShellSectionRenderer(ShellSection shellSection) => throw new NotSupportedException();

		public IShellNavBarAppearanceTracker CreateNavBarAppearanceTracker() => throw new NotSupportedException();

		public IShellTabBarAppearanceTracker CreateTabBarAppearanceTracker() => throw new NotSupportedException();

		public IShellSearchResultsRenderer CreateShellSearchResultsRenderer() => throw new NotSupportedException();
	}
}
