using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using CoreGraphics;
using Foundation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Maui.Handlers;
using UIKit;

namespace MauiWindowFrameObserverLeakRepro;

public sealed class LeakProbePage : ContentPage
{
	const int CycleCount = 20;

	static readonly PropertyMapper<IWindow, IWindowHandler> EmptyWindowMapper = new();
	static readonly FieldInfo FrameObserverProxyField =
		typeof(WindowHandler).GetField("_frameObserverProxy", BindingFlags.Instance | BindingFlags.NonPublic)
		?? throw new MissingFieldException(typeof(WindowHandler).FullName, "_frameObserverProxy");

	readonly Label _status;
	readonly VerticalStackLayout _log;
	bool _started;

	public LeakProbePage()
	{
		Title = "Window frame observer leak repro";
		BackgroundColor = Colors.White;

		_status = new Label
		{
			Text = "Waiting to start...",
			TextColor = Colors.Black,
			FontSize = 16,
			LineBreakMode = LineBreakMode.WordWrap
		};

		_log = new VerticalStackLayout
		{
			Spacing = 4
		};

		Content = new ScrollView
		{
			Content = new VerticalStackLayout
			{
				Padding = new Thickness(20),
				Spacing = 12,
				Children =
				{
					new Label
					{
						Text = "MAUI Window Frame Observer Leak Repro",
						TextColor = Colors.Black,
						FontSize = 22,
						FontAttributes = FontAttributes.Bold
					},
					_status,
					_log
				}
			}
		};
	}

	protected override async void OnAppearing()
	{
		base.OnAppearing();

		if (_started)
			return;

		_started = true;
		await Task.Delay(500);

		try
		{
			await RunAsync();
		}
		catch (Exception ex)
		{
			Log("ERROR: " + ex);
			_status.Text = "Repro failed: " + ex.Message;
			await ExitAsync(3);
		}
	}

	async Task RunAsync()
	{
		Log("Running control scenario: call FrameObserverProxy.Disconnect after handler disconnect.");
		var control = await RunScenarioAsync("manual-frame-observer-disconnect", forceFrameObserverDisconnect: true);

		Log("Running suspect scenario: current WindowHandler.DisconnectHandler behavior.");
		var suspect = await RunScenarioAsync("current-disconnect", forceFrameObserverDisconnect: false);

		var applicable =
#if IOS
			true;
#elif MACCATALYST
			!OperatingSystem.IsMacCatalystVersionAtLeast(16);
#else
			false;
#endif

		var proof =
			applicable &&
			control.FrameCallbacksAfterDisconnect == 0 &&
			control.FrameObserverProxyAlive == 0 &&
			control.KvoTokenAlive == 0 &&
			suspect.FrameCallbacksAfterDisconnect > 0 &&
			suspect.FrameObserverProxyAlive > 0 &&
			suspect.KvoTokenAlive > 0;

		var notApplicable = !applicable && suspect.FrameCallbacksAfterDisconnect == 0;
		var resultPath = WriteResultFile(control, suspect, proof, notApplicable);
		var summary =
			$"RESULT: {(proof ? "LEAK REPRODUCED" : notApplicable ? "NOT APPLICABLE ON THIS RUNTIME" : "NOT PROVEN")}\n" +
			$"Control: callbacks-after-disconnect={control.FrameCallbacksAfterDisconnect}, proxy-alive={control.FrameObserverProxyAlive}/{CycleCount}, kvo-token-alive={control.KvoTokenAlive}/{CycleCount}\n" +
			$"Suspect: callbacks-after-disconnect={suspect.FrameCallbacksAfterDisconnect}, proxy-alive={suspect.FrameObserverProxyAlive}/{CycleCount}, kvo-token-alive={suspect.KvoTokenAlive}/{CycleCount}\n" +
			$"Result file: {resultPath}";

		_status.Text = summary;
		Log(summary);

		await ExitAsync(proof ? 0 : notApplicable ? 4 : 2);
	}

	async Task<ScenarioResult> RunScenarioAsync(string name, bool forceFrameObserverDisconnect)
	{
		var services = Handler?.MauiContext?.Services
			?? throw new InvalidOperationException("The page does not have a MAUI service provider.");
		var probes = new List<ProbeRefs>();
		var retainedPlatformWindows = new List<UIWindow>();
		var frameCallbacksAfterDisconnect = 0;

		for (var i = 0; i < CycleCount; i++)
		{
			var probe = CreateDisconnectedWindowProbe(services, name, forceFrameObserverDisconnect, i, retainedPlatformWindows);
			probes.Add(probe);
			frameCallbacksAfterDisconnect += probe.FrameCallbacksAfterDisconnect;
			await ForceGcAsync();
			Log($"{name} cycle {i + 1}: callbacks={probe.FrameCallbacksAfterDisconnect}, proxy alive={probes.Count(p => p.FrameObserverProxy.IsAlive)}");
		}

		await ForceGcAsync();
		await ForceGcAsync();

		return new ScenarioResult(
			name,
			frameCallbacksAfterDisconnect,
			probes.Count(p => p.FrameObserverProxy.IsAlive),
			probes.Count(p => p.KvoToken.IsAlive),
			probes.Count(p => p.Handler.IsAlive),
			probes.Count(p => p.VirtualWindow.IsAlive),
			retainedPlatformWindows);
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	static ProbeRefs CreateDisconnectedWindowProbe(
		IServiceProvider services,
		string scenarioName,
		bool forceFrameObserverDisconnect,
		int index,
		List<UIWindow> retainedPlatformWindows)
	{
		var platformWindow = new UIWindow(new CGRect(0, 0, 320, 480));
		var mauiContext = new FixedWindowMauiContext(services, platformWindow);
		var virtualWindow = new ProbeWindow();
		var handler = new WindowHandler(EmptyWindowMapper);

		handler.SetMauiContext(mauiContext);
		handler.SetVirtualView(virtualWindow);

		var frameObserverProxy = FrameObserverProxyField.GetValue(handler)
			?? throw new InvalidOperationException("WindowHandler did not create a FrameObserverProxy.");
		var kvoToken = GetKvoToken(frameObserverProxy);

		((IElementHandler)handler).DisconnectHandler();

		if (forceFrameObserverDisconnect)
			DisconnectFrameObserverProxy(frameObserverProxy, platformWindow);

		virtualWindow.ResetFrameChanges();
		platformWindow.Frame = new CGRect(10 + index, 20 + index, 330 + index, 490 + index);

		retainedPlatformWindows.Add(platformWindow);

		return new ProbeRefs(
			new WeakReference(frameObserverProxy),
			new WeakReference(kvoToken),
			new WeakReference(handler),
			new WeakReference(virtualWindow),
			virtualWindow.FrameChangedCount);
	}

	static object GetKvoToken(object frameObserverProxy)
	{
		var field = frameObserverProxy.GetType().GetField("_frameObserver", BindingFlags.Instance | BindingFlags.NonPublic)
			?? throw new MissingFieldException(frameObserverProxy.GetType().FullName, "_frameObserver");

		return field.GetValue(frameObserverProxy)
			?? throw new InvalidOperationException("FrameObserverProxy did not create a KVO token.");
	}

	static void DisconnectFrameObserverProxy(object frameObserverProxy, UIWindow platformWindow)
	{
		var method = frameObserverProxy.GetType().GetMethod("Disconnect", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
			?? throw new MissingMethodException(frameObserverProxy.GetType().FullName, "Disconnect");

		method.Invoke(frameObserverProxy, [platformWindow]);
	}

	static async Task ForceGcAsync()
	{
		for (var i = 0; i < 4; i++)
		{
			GC.Collect();
			GC.WaitForPendingFinalizers();
			GC.Collect(2, GCCollectionMode.Forced, blocking: true);
			await Task.Delay(50);
		}
	}

	string WriteResultFile(ScenarioResult control, ScenarioResult suspect, bool proof, bool notApplicable)
	{
		var path = Path.Combine(FileSystem.AppDataDirectory, "window-frame-observer-leak-result.txt");
		var text =
			$"WindowHandler frame observer leak repro\n" +
			$"Timestamp: {DateTimeOffset.Now:O}\n" +
			$"Proof: {proof}\n" +
			$"NotApplicable: {notApplicable}\n" +
			$"{control}\n" +
			$"{suspect}\n";

		File.WriteAllText(path, text);
		return path;
	}

	void Log(string message)
	{
		Debug.WriteLine(message);
		Console.WriteLine(message);

		_log.Children.Add(new Label
		{
			Text = message,
			TextColor = Colors.Black,
			FontSize = 12,
			LineBreakMode = LineBreakMode.WordWrap
		});
	}

	static async Task ExitAsync(int exitCode)
	{
		if (Environment.GetEnvironmentVariable("MAUI_REPRO_NO_EXIT") == "1")
			return;

		await Task.Delay(1000);
		Environment.Exit(exitCode);
	}

	readonly record struct ProbeRefs(
		WeakReference FrameObserverProxy,
		WeakReference KvoToken,
		WeakReference Handler,
		WeakReference VirtualWindow,
		int FrameCallbacksAfterDisconnect);

	sealed record ScenarioResult(
		string Name,
		int FrameCallbacksAfterDisconnect,
		int FrameObserverProxyAlive,
		int KvoTokenAlive,
		int HandlerAlive,
		int VirtualWindowAlive,
		List<UIWindow> RetainedPlatformWindows)
	{
		public override string ToString() =>
			$"{Name}: callbacks-after-disconnect={FrameCallbacksAfterDisconnect}, " +
			$"proxy-alive={FrameObserverProxyAlive}, kvo-token-alive={KvoTokenAlive}, " +
			$"handler-alive={HandlerAlive}, virtual-window-alive={VirtualWindowAlive}, " +
			$"retained-platform-windows={RetainedPlatformWindows.Count}";
	}

	sealed class FixedWindowMauiContext : IMauiContext, IServiceProvider
	{
		readonly IServiceProvider _services;
		readonly UIWindow _platformWindow;
		IMauiHandlersFactory? _handlers;

		public FixedWindowMauiContext(IServiceProvider services, UIWindow platformWindow)
		{
			_services = services;
			_platformWindow = platformWindow;
		}

		public IServiceProvider Services => this;

		public IMauiHandlersFactory Handlers => _handlers ??= _services.GetRequiredService<IMauiHandlersFactory>();

		public object? GetService(Type serviceType)
		{
			if (serviceType == typeof(UIWindow))
				return _platformWindow;

			return _services.GetService(serviceType);
		}
	}

	sealed class ProbeWindow : IWindow
	{
		readonly HashSet<IWindowOverlay> _overlays = [];

		public IElementHandler? Handler { get; set; }

		public IElement? Parent => null;

		public string? Title => "Probe";

		public IView? Content => null;

		public IVisualDiagnosticsOverlay VisualDiagnosticsOverlay => null!;

		public IReadOnlyCollection<IWindowOverlay> Overlays => _overlays;

		public double X { get; private set; } = double.NaN;

		public double Y { get; private set; } = double.NaN;

		public double Width { get; private set; } = double.NaN;

		public double Height { get; private set; } = double.NaN;

		public double MinimumWidth => double.NaN;

		public double MaximumWidth => double.NaN;

		public double MinimumHeight => double.NaN;

		public double MaximumHeight => double.NaN;

		public FlowDirection FlowDirection => FlowDirection.LeftToRight;

		public int FrameChangedCount { get; private set; }

		public bool AddOverlay(IWindowOverlay overlay) => _overlays.Add(overlay);

		public bool RemoveOverlay(IWindowOverlay overlay) => _overlays.Remove(overlay);

		public void Created()
		{
		}

		public void Resumed()
		{
		}

		public void Activated()
		{
		}

		public void Deactivated()
		{
		}

		public void Stopped()
		{
		}

		public void Destroying()
		{
		}

		public void Backgrounding(IPersistedState state)
		{
		}

		public bool BackButtonClicked() => true;

		public void DisplayDensityChanged(float displayDensity)
		{
		}

		public void FrameChanged(Rect frame)
		{
			FrameChangedCount++;
			X = frame.X;
			Y = frame.Y;
			Width = frame.Width;
			Height = frame.Height;
		}

		public float RequestDisplayDensity() => 1.0f;

		public void ResetFrameChanges() => FrameChangedCount = 0;
	}
}
