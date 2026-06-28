#nullable enable

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using CoreGraphics;
using Foundation;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Controls.Compatibility.Platform.iOS;
using Microsoft.Maui.Graphics;
using ObjCRuntime;
using UIKit;

namespace IosButtonRendererBackgroundPatternRetentionRepro;

internal static class ReproSession
{
	internal const int Cycles = 24;
	internal const int ButtonWidthPoints = 384;
	internal const int ButtonHeightPoints = 128;
	internal const int PayloadMegabytesPerCycle = 1;

	static readonly IntPtr RetainSelector = Selector.GetHandle("retain");

	static readonly List<IReadOnlyList<RetainedButtonPeer>> RetainedNativeButtons = new();

	public static readonly string ResultsPath =
		Path.Combine("/tmp", "ios-buttonrenderer-background-pattern-retention-results.txt");

	public static ReproReport Run(IMauiContext mauiContext)
	{
		WriteProgress("Starting iOS ButtonRenderer background pattern retention repro.");
		ForceFullGc();
		var baselineBytes = GC.GetTotalMemory(true);

		WriteProgress("Running control scenario.");
		var control = RunScenario(
			"control: clear native UIButton background before renderer disposal",
			clearNativeBackgroundBeforeDispose: true,
			mauiContext);

		WriteProgress("Running current MAUI scenario.");
		var current = RunScenario(
			"current: ButtonRenderer dispose leaves native UIButton pattern background assigned",
			clearNativeBackgroundBeforeDispose: false,
			mauiContext);

		WriteProgress("Finalizing report.");
		ForceFullGc();
		var finalBytes = GC.GetTotalMemory(true);

		GC.KeepAlive(RetainedNativeButtons);

		return new ReproReport(
			Cycles,
			ButtonWidthPoints,
			ButtonHeightPoints,
			GetDisplayScale(),
			PayloadMegabytesPerCycle,
			baselineBytes,
			finalBytes,
			control,
			current);
	}

	static ScenarioResult RunScenario(
		string name,
		bool clearNativeBackgroundBeforeDispose,
		IMauiContext mauiContext)
	{
		var tracking = RunScenarioCore(name, clearNativeBackgroundBeforeDispose, mauiContext);
		RetainedNativeButtons.Add(tracking.NativeButtons);
		ForceFullGc();

		return ScenarioResult.From(name, tracking.NativeButtons, tracking.TrackedCycles);
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	static ScenarioTracking RunScenarioCore(
		string name,
		bool clearNativeBackgroundBeforeDispose,
		IMauiContext mauiContext)
	{
		var nativeButtons = new List<RetainedButtonPeer>(Cycles);
		var tracked = new List<TrackedCycle>(Cycles);

		for (var i = 0; i < Cycles; i++)
		{
			if (i % 6 == 0)
				WriteProgress($"{name}: cycle {i}/{Cycles}");

			CreateDisposedRendererCycle(i, nativeButtons, tracked, clearNativeBackgroundBeforeDispose, mauiContext);
		}

		return new ScenarioTracking(nativeButtons, tracked);
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	static void CreateDisposedRendererCycle(
		int cycle,
		List<RetainedButtonPeer> nativeButtons,
		List<TrackedCycle> tracked,
		bool clearNativeBackgroundBeforeDispose,
		IMauiContext mauiContext)
	{
		var payload = new ButtonPayload(cycle, PayloadMegabytesPerCycle * 1024L * 1024L);
		var brush = CreateBackground(cycle);
		var button = new PayloadButton(cycle, payload, brush);
		var contextHandler = new ContextOnlyHandler(mauiContext);
		var renderer = new ButtonRenderer();

		button.Layout(new Rect(0, 0, ButtonWidthPoints, ButtonHeightPoints));
		contextHandler.SetVirtualView(button);
		SetRealisticBounds(renderer);
		renderer.SetElement(button);
		renderer.LayoutSubviews();

		var nativeButton = renderer.Control
			?? throw new InvalidOperationException("ButtonRenderer did not create a native UIButton.");

		if (!HasPatternBackground(nativeButton))
			throw new InvalidOperationException("ButtonRenderer did not assign a pattern-image background color.");

		var retainedNativeButton = RetainNativeButton(nativeButton);

		if (clearNativeBackgroundBeforeDispose)
			nativeButton.BackgroundColor = UIColor.Clear;

		renderer.Dispose();
		((IElementController)button).EffectControlProvider = null;
		contextHandler.DisconnectHandler();

		nativeButtons.Add(retainedNativeButton);
		tracked.Add(TrackedCycle.Create(cycle, renderer, button, payload, brush));
	}

	static void SetRealisticBounds(ButtonRenderer renderer)
	{
		var bounds = new CGRect(0, 0, ButtonWidthPoints, ButtonHeightPoints);
		renderer.Frame = bounds;
		renderer.Bounds = bounds;
	}

	static Brush CreateBackground(int cycle)
	{
		return new LinearGradientBrush
		{
			StartPoint = new Point(0, 0),
			EndPoint = new Point(1, 1),
			GradientStops =
			{
				new GradientStop(Color.FromRgb((cycle * 29) % 255, 64, 132), 0),
				new GradientStop(Color.FromRgb(240, (cycle * 47) % 255, 40), 0.48f),
				new GradientStop(Color.FromRgb(28, 34, (cycle * 83) % 255), 1)
			}
		};
	}

	static RetainedButtonPeer RetainNativeButton(UIButton button)
	{
		var handle = button.Handle;
		if (handle == IntPtr.Zero)
			throw new InvalidOperationException("Cannot retain a native UIButton with a zero handle.");

		var retained = IntPtr_objc_msgSend(handle, RetainSelector);
		if (retained == IntPtr.Zero)
			throw new InvalidOperationException("Objective-C retain returned a zero handle.");

		return new RetainedButtonPeer(retained);
	}

	static bool HasPatternBackground(UIButton button)
	{
		var color = button.BackgroundColor;
		if (color is null)
			return false;

		try
		{
			if (color.CGColor?.Pattern is not null)
				return true;
		}
		catch
		{
			return true;
		}

		nfloat red;
		nfloat green;
		nfloat blue;
		nfloat alpha;

		try
		{
			color.GetRGBA(out red, out green, out blue, out alpha);
		}
		catch
		{
			return true;
		}

		const double tolerance = 0.001;
		return Math.Abs(red) > tolerance ||
			Math.Abs(green) > tolerance ||
			Math.Abs(blue) > tolerance ||
			Math.Abs(alpha) > tolerance;
	}

	static nfloat GetDisplayScale() => UIScreen.MainScreen.Scale <= 0 ? 1 : UIScreen.MainScreen.Scale;

	static long EstimatePatternImageBytes()
	{
		var scale = GetDisplayScale();
		var width = Math.Max(1, (int)Math.Ceiling(ButtonWidthPoints * scale));
		var height = Math.Max(1, (int)Math.Ceiling(ButtonHeightPoints * scale));
		return width * (long)height * 4;
	}

	static void ForceFullGc()
	{
		for (var i = 0; i < 4; i++)
		{
			using var pool = new NSAutoreleasePool();
			GC.Collect();
			GC.WaitForPendingFinalizers();
			GC.Collect();
			Thread.Sleep(100);
		}
	}

	static void WriteProgress(string message)
	{
		try
		{
			File.WriteAllText(ResultsPath, message + Environment.NewLine);
		}
		catch
		{
			// Progress output is diagnostic only; the final report write remains authoritative.
		}
	}

	[DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
	static extern IntPtr IntPtr_objc_msgSend(IntPtr receiver, IntPtr selector);

	internal sealed record ScenarioTracking(
		IReadOnlyList<RetainedButtonPeer> NativeButtons,
		IReadOnlyList<TrackedCycle> TrackedCycles);

	internal sealed class RetainedButtonPeer
	{
		public RetainedButtonPeer(IntPtr handle)
		{
			Handle = handle;
		}

		public IntPtr Handle { get; }

		public UIButton? TryGetButton()
		{
			if (Handle == IntPtr.Zero)
				return null;

			try
			{
				var button = Runtime.GetNSObject<UIButton>(Handle, false);
				return button?.Handle == IntPtr.Zero ? null : button;
			}
			catch
			{
				return null;
			}
		}
	}

	internal sealed record TrackedCycle(
		int Cycle,
		WeakReference<ButtonRenderer> Renderer,
		WeakReference<Button> Button,
		WeakReference<ButtonPayload> Payload,
		WeakReference<Brush> Brush,
		long PayloadBytes)
	{
		public static TrackedCycle Create(
			int cycle,
			ButtonRenderer renderer,
			Button button,
			ButtonPayload payload,
			Brush brush)
		{
			return new TrackedCycle(
				cycle,
				new WeakReference<ButtonRenderer>(renderer),
				new WeakReference<Button>(button),
				new WeakReference<ButtonPayload>(payload),
				new WeakReference<Brush>(brush),
				payload.PayloadBytes);
		}
	}

	internal sealed record ScenarioResult(
		string Name,
		int TrackedCycles,
		int RetainedNativeButtonPeers,
		int NativeButtonsWithPatternBackground,
		long EstimatedPatternImageBytes,
		int AliveRenderers,
		int AliveButtons,
		int AlivePayloads,
		int AliveBrushes,
		long RetainedPayloadBytes)
	{
		internal static ScenarioResult From(
			string name,
			IReadOnlyList<RetainedButtonPeer> nativeButtons,
			IReadOnlyList<TrackedCycle> tracked)
		{
			var aliveNativeButtons = 0;
			var nativeButtonsWithPatternBackground = 0;
			foreach (var peer in nativeButtons)
			{
				var button = peer.TryGetButton();
				if (button is null)
					continue;

				aliveNativeButtons++;
				if (HasPatternBackground(button))
					nativeButtonsWithPatternBackground++;
			}

			var aliveRenderers = 0;
			var aliveButtons = 0;
			var alivePayloads = 0;
			var aliveBrushes = 0;
			long retainedPayloadBytes = 0;

			foreach (var cycle in tracked)
			{
				if (cycle.Renderer.TryGetTarget(out _))
					aliveRenderers++;

				if (cycle.Button.TryGetTarget(out _))
					aliveButtons++;

				if (cycle.Payload.TryGetTarget(out _))
				{
					alivePayloads++;
					retainedPayloadBytes += cycle.PayloadBytes;
				}

				if (cycle.Brush.TryGetTarget(out _))
					aliveBrushes++;
			}

			return new ScenarioResult(
				name,
				tracked.Count,
				aliveNativeButtons,
				nativeButtonsWithPatternBackground,
				nativeButtonsWithPatternBackground * EstimatePatternImageBytes(),
				aliveRenderers,
				aliveButtons,
				alivePayloads,
				aliveBrushes,
				retainedPayloadBytes);
		}
	}
}

internal sealed class ContextOnlyHandler : IPlatformViewHandler
{
	readonly UIView _platformView = new();

	public ContextOnlyHandler(IMauiContext mauiContext)
	{
		MauiContext = mauiContext;
	}

	public object? PlatformView => _platformView;

	public bool HasContainer { get; set; }

	public object? ContainerView => null;

	UIView? IPlatformViewHandler.PlatformView => _platformView;

	UIView? IPlatformViewHandler.ContainerView => null;

	public UIViewController? ViewController => null;

	public IElement? VirtualView { get; private set; }

	IView? IViewHandler.VirtualView => VirtualView as IView;

	public IMauiContext? MauiContext { get; private set; }

	public void SetMauiContext(IMauiContext mauiContext) => MauiContext = mauiContext;

	public void SetVirtualView(IElement view)
	{
		VirtualView = view;
		if (view.Handler != this)
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

	public Size GetDesiredSize(double widthConstraint, double heightConstraint) => Size.Zero;

	public void PlatformArrange(Rect frame)
	{
	}
}

internal sealed record ReproReport(
	int Cycles,
	int ButtonWidthPoints,
	int ButtonHeightPoints,
	nfloat DisplayScale,
	int PayloadMegabytesPerCycle,
	long BaselineManagedBytes,
	long FinalManagedBytes,
	ReproSession.ScenarioResult Control,
	ReproSession.ScenarioResult Current)
{
	public bool LeakProved =>
		Control.RetainedNativeButtonPeers == Cycles &&
		Control.NativeButtonsWithPatternBackground == 0 &&
		Control.AliveButtons == 0 &&
		Control.AlivePayloads == 0 &&
		Control.AliveBrushes == 0 &&
		Current.RetainedNativeButtonPeers == Cycles &&
		Current.NativeButtonsWithPatternBackground == Cycles &&
		Current.EstimatedPatternImageBytes > 0 &&
		Current.AliveButtons == 0 &&
		Current.AlivePayloads == 0 &&
		Current.AliveBrushes == 0;

	public string ToText()
	{
		var retainedMiB = Current.EstimatedPatternImageBytes / 1024d / 1024d;
		var controlMiB = Control.EstimatedPatternImageBytes / 1024d / 1024d;
		var heapDeltaMiB = (FinalManagedBytes - BaselineManagedBytes) / 1024d / 1024d;

		return string.Join(Environment.NewLine,
			"IosButtonRendererBackgroundPatternRetentionRepro",
			$"Cycles: {Cycles}",
			$"Button size: {ButtonWidthPoints} x {ButtonHeightPoints} points",
			$"Display scale: {DisplayScale:N1}",
			$"Payload size per Button: {PayloadMegabytesPerCycle} MiB",
			$"Baseline managed heap: {BaselineManagedBytes:N0} bytes",
			$"Final managed heap: {FinalManagedBytes:N0} bytes",
			$"Managed heap delta: {heapDeltaMiB:N1} MiB",
			$"Leak proved: {LeakProved}",
			string.Empty,
			Format(Control),
			string.Empty,
			Format(Current),
			string.Empty,
			$"Control estimated assigned native pattern image payload: {controlMiB:N1} MiB",
			$"Current estimated assigned native pattern image payload: {retainedMiB:N1} MiB",
			$"RESULT: {(LeakProved ? "PROVEN" : "NOT PROVEN")}");
	}

	static string Format(ReproSession.ScenarioResult result)
	{
		var retainedMiB = result.EstimatedPatternImageBytes / 1024d / 1024d;
		var payloadMiB = result.RetainedPayloadBytes / 1024d / 1024d;

		return string.Join(Environment.NewLine,
			$"Run: {result.Name}",
			$"  tracked cycles: {result.TrackedCycles}",
			$"  retained native UIButton peers: {result.RetainedNativeButtonPeers}/{result.TrackedCycles}",
			$"  native buttons with pattern background: {result.NativeButtonsWithPatternBackground}/{result.TrackedCycles}",
			$"  estimated assigned native image bytes: {result.EstimatedPatternImageBytes:N0}",
			$"  estimated assigned native image MiB: {retainedMiB:N1}",
			$"  alive renderers: {result.AliveRenderers}/{result.TrackedCycles}",
			$"  alive Buttons: {result.AliveButtons}/{result.TrackedCycles}",
			$"  alive payloads: {result.AlivePayloads}/{result.TrackedCycles}",
			$"  alive brushes: {result.AliveBrushes}/{result.TrackedCycles}",
			$"  retained managed payload MiB: {payloadMiB:N1}");
	}
}

internal sealed class PayloadButton : Button
{
	public PayloadButton(int cycle, ButtonPayload payload, Brush background)
	{
		Cycle = cycle;
		Payload = payload;
		Text = string.Empty;
		Background = background;
		WidthRequest = ReproSession.ButtonWidthPoints;
		HeightRequest = ReproSession.ButtonHeightPoints;
	}

	public int Cycle { get; }

	public ButtonPayload Payload { get; }
}

internal sealed class ButtonPayload
{
	readonly byte[] _bytes;

	public ButtonPayload(int cycle, long bytes)
	{
		_bytes = new byte[checked((int)bytes)];
		for (var i = 0; i < _bytes.Length; i += 4096)
			_bytes[i] = (byte)((cycle + i) % 251);
	}

	public long PayloadBytes => _bytes.LongLength;
}
