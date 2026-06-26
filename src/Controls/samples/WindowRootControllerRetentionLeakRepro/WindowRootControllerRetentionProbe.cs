using Microsoft.Extensions.DependencyInjection;
using Microsoft.Maui;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Handlers;
using Foundation;
using System.Runtime.CompilerServices;
using UIKit;

namespace WindowRootControllerRetentionLeakRepro;

static class WindowRootControllerRetentionProbe
{
	const int Iterations = 80;
	const int PayloadBytes = 1024 * 1024;

	public static ProbeResult Run()
	{
		var services = ((IPlatformApplication)IPlatformApplication.Current!).Services;
		var controlScenes = new List<SceneDelegateStandIn>(Iterations);
		var currentScenes = new List<SceneDelegateStandIn>(Iterations);
		var control = new List<ScenarioRefs>(Iterations);
		var current = new List<ScenarioRefs>(Iterations);
		var rootPayloads = new ConditionalWeakTable<UIViewController, Payload>();

		for (var i = 0; i < Iterations; i++)
			control.Add(CreateScenario(services, controlScenes, rootPayloads, releaseSceneWindow: true, i));

		for (var i = 0; i < Iterations; i++)
			current.Add(CreateScenario(services, currentScenes, rootPayloads, releaseSceneWindow: false, i));

		ForceCollect();

		return new ProbeResult(
			Iterations,
			PayloadBytes,
			CountAlive(control, static r => r.RootPayload),
			CountAlive(control, static r => r.RootController),
			controlScenes.Count(static s => s.Window?.RootViewController is not null),
			CountAlive(current, static r => r.RootPayload),
			CountAlive(current, static r => r.RootController),
			currentScenes.Count(static s => s.Window?.RootViewController is not null),
			GC.GetTotalMemory(forceFullCollection: true));
	}

	static ScenarioRefs CreateScenario(
		IServiceProvider services,
		List<SceneDelegateStandIn> retainedScenes,
		ConditionalWeakTable<UIViewController, Payload> rootPayloads,
		bool releaseSceneWindow,
		int index)
	{
		using var pool = new NSAutoreleasePool();
#pragma warning disable CA1422 // The repro intentionally creates standalone retained UIWindows.
		var nativeWindow = new UIWindow(UIScreen.MainScreen.Bounds);
#pragma warning restore CA1422
		var context = new MauiContext(new PlatformWindowServiceProvider(services, nativeWindow));
		var payload = new Payload(index, PayloadBytes);
		var page = new ContentPage
		{
			Title = $"Transient page {index}",
			Content = new Label { Text = $"Transient payload page {index}" }
		};
		var mauiWindow = new Window(page);
		var handler = new WindowHandler();

		handler.SetMauiContext(context);
		handler.SetVirtualView(mauiWindow);

		var rootController = nativeWindow.RootViewController;
		rootPayloads.Add(rootController!, payload);
		var refs = new ScenarioRefs(
			new WeakReference<Payload>(payload),
			new WeakReference<UIViewController>(rootController!));
		var sceneDelegate = new SceneDelegateStandIn(nativeWindow);

		retainedScenes.Add(sceneDelegate);
		((IWindow)mauiWindow).Destroying();

		if (releaseSceneWindow)
		{
			page.DisconnectHandlers();
			sceneDelegate.Window = null;
			nativeWindow.RootViewController = null;
			DisposeControllerTree(rootController);
			nativeWindow.Dispose();
		}

		return refs;
	}

	static int CountAlive<T>(List<ScenarioRefs> refs, Func<ScenarioRefs, WeakReference<T>> selector)
		where T : class
	{
		var count = 0;
		foreach (var item in refs)
		{
			if (selector(item).TryGetTarget(out _))
				count++;
		}

		return count;
	}

	static void ForceCollect()
	{
		for (var i = 0; i < 8; i++)
		{
			GC.Collect();
			GC.WaitForPendingFinalizers();
			GC.Collect();
			Thread.Sleep(50);
		}
	}

	static void DisposeControllerTree(UIViewController? controller)
	{
		if (controller is null)
			return;

		foreach (var child in controller.ChildViewControllers.ToArray())
		{
			child.WillMoveToParentViewController(null);
			child.View?.RemoveFromSuperview();
			child.RemoveFromParentViewController();
			DisposeControllerTree(child);
		}

		controller.View?.RemoveFromSuperview();
		controller.Dispose();
	}

	sealed class PlatformWindowServiceProvider : IServiceProvider, IKeyedServiceProvider
	{
		readonly IServiceProvider _inner;
		readonly UIWindow _window;

		public PlatformWindowServiceProvider(IServiceProvider inner, UIWindow window)
		{
			_inner = inner;
			_window = window;
		}

		public object? GetService(Type serviceType)
		{
			if (serviceType == typeof(UIWindow))
				return _window;

			return _inner.GetService(serviceType);
		}

		public object? GetKeyedService(Type serviceType, object? serviceKey) =>
			_inner is IKeyedServiceProvider keyed
				? keyed.GetKeyedService(serviceType, serviceKey)
				: null;

		public object GetRequiredKeyedService(Type serviceType, object? serviceKey) =>
			_inner is IKeyedServiceProvider keyed
				? keyed.GetRequiredKeyedService(serviceType, serviceKey)
				: throw new InvalidOperationException($"No keyed service provider is available for {serviceType}.");
	}

	sealed class Payload
	{
		readonly byte[] _bytes;

		public Payload(int id, int size)
		{
			Id = id;
			_bytes = new byte[size];
			_bytes[0] = (byte)(id % 251);
			_bytes[^1] = (byte)((id + 17) % 251);
		}

		public int Id { get; }
	}

	sealed class SceneDelegateStandIn
	{
		public SceneDelegateStandIn(UIWindow window)
		{
			Window = window;
		}

		public UIWindow? Window { get; set; }
	}

	sealed record ScenarioRefs(
		WeakReference<Payload> RootPayload,
		WeakReference<UIViewController> RootController);
}

sealed record ProbeResult(
	int Iterations,
	int PayloadBytes,
	int ControlRootPayloadsRetained,
	int ControlRootControllersRetained,
	int ControlSceneWindowsWithRootController,
	int CurrentRootPayloadsRetained,
	int CurrentRootControllersRetained,
	int CurrentSceneWindowsWithRootController,
	long ManagedHeapBytes)
{
	public bool ProvedLeak =>
		ControlRootPayloadsRetained == 0 &&
		CurrentRootPayloadsRetained == Iterations &&
		CurrentRootControllersRetained == Iterations &&
		CurrentSceneWindowsWithRootController == Iterations;

	public string ToReport()
	{
		var retainedMiB = CurrentRootPayloadsRetained * PayloadBytes / 1024.0 / 1024.0;
		var heapMiB = ManagedHeapBytes / 1024.0 / 1024.0;

		return string.Join(Environment.NewLine, new[]
		{
			"WindowRootControllerRetentionLeakRepro",
			$"Iterations: {Iterations}",
			$"Payload bytes per page: {PayloadBytes}",
			$"Control retained root-controller payloads: {ControlRootPayloadsRetained}/{Iterations}",
			$"Control retained root controllers: {ControlRootControllersRetained}/{Iterations}",
			$"Control retained scene windows still carrying root controllers: {ControlSceneWindowsWithRootController}/{Iterations}",
			$"Current retained root-controller payloads: {CurrentRootPayloadsRetained}/{Iterations}",
			$"Current retained root controllers: {CurrentRootControllersRetained}/{Iterations}",
			$"Current retained scene windows still carrying root controllers: {CurrentSceneWindowsWithRootController}/{Iterations}",
			$"Retained payload estimate: {retainedMiB:F1} MiB",
			$"Managed heap after proof: {heapMiB:F1} MiB",
			$"Proved leak: {ProvedLeak}"
		});
	}
}
