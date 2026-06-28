#nullable enable

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using CoreGraphics;
using Foundation;
using Microsoft.Maui;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Controls.Compatibility.Platform.iOS;
using ObjCRuntime;
using UIKit;
using CompatibilityButtonRenderer = Microsoft.Maui.Controls.Compatibility.Platform.iOS.ButtonRenderer;

namespace IosButtonRendererTitleRetentionRepro;

internal static class ReproSession
{
	internal const int Cycles = 4096;
	internal const int PayloadKiBPerButton = 1;
	const long PayloadBytesPerButton = PayloadKiBPerButton * 1024L;

	static readonly IntPtr RetainSelector = Selector.GetHandle("retain");
	static readonly List<IReadOnlyList<RetainedButtonPeer>> RetainedNativePeers = new();
	static readonly UIControlState[] TitleStates =
	[
		UIControlState.Normal,
		UIControlState.Highlighted,
		UIControlState.Disabled,
		UIControlState.Selected,
		UIControlState.Focused
	];

	public static readonly string ResultsPath =
		Path.Combine("/tmp", "ios-buttonrenderer-title-retention-results.txt");

	public static ReproReport Run(IMauiContext mauiContext)
	{
		WriteProgress("Starting iOS ButtonRenderer title retention repro.");
		ForceFullGc();
		var baselineBytes = GC.GetTotalMemory(true);

		WriteProgress("Running control scenario.");
		var control = RunScenario(
			"control: clear native UIButton title slots before renderer disposal",
			mauiContext,
			clearNativeTitleBeforeDispose: true);

		WriteProgress("Running current MAUI scenario.");
		var current = RunScenario(
			"current: renderer disposal leaves native UIButton title slots assigned",
			mauiContext,
			clearNativeTitleBeforeDispose: false);

		WriteProgress("Finalizing report.");
		ForceFullGc();
		var finalBytes = GC.GetTotalMemory(true);

		GC.KeepAlive(RetainedNativePeers);

		return new ReproReport(
			Cycles,
			PayloadKiBPerButton,
			baselineBytes,
			finalBytes,
			control,
			current);
	}

	static ScenarioResult RunScenario(
		string name,
		IMauiContext mauiContext,
		bool clearNativeTitleBeforeDispose)
	{
		var retainedPeers = new List<RetainedButtonPeer>(Cycles);
		var tracked = new List<TrackedCycle>(Cycles);

		for (var i = 0; i < Cycles; i++)
		{
			if (i % 12 == 0)
				WriteProgress($"{name}: cycle {i}/{Cycles}");

			CreateButtonRendererCycle(i, mauiContext, retainedPeers, tracked, clearNativeTitleBeforeDispose);
		}

		RetainedNativePeers.Add(retainedPeers);
		ForceFullGc();

		return ScenarioResult.From(name, retainedPeers, tracked);
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	static void CreateButtonRendererCycle(
		int cycle,
		IMauiContext mauiContext,
		List<RetainedButtonPeer> retainedPeers,
		List<TrackedCycle> tracked,
		bool clearNativeTitleBeforeDispose)
	{
		using var pool = new NSAutoreleasePool();

		var title = CreateOperationalTitle(cycle);
		var button = new Button
		{
			AutomationId = $"buttonrenderer-title-{cycle:000}",
			Text = title,
			WidthRequest = 720,
			HeightRequest = 64
		};
		button.Layout(new Rect(0, 0, 720, 64));

		var contextHandler = new ContextOnlyHandler(mauiContext);
		contextHandler.SetVirtualView(button);

		var renderer = new LeakProbeButtonRenderer();
		SetRealisticBounds(renderer, 720, 64);
		renderer.SetElement(button);

		var nativeButton = renderer.Control
			?? throw new InvalidOperationException("ButtonRenderer did not create a native UIButton.");
		SetRealisticBounds(nativeButton, 720, 64);
		StabilizeNativeLabelLayout(nativeButton);

		if (!NativeTitleHasPayload(nativeButton))
			throw new InvalidOperationException("ButtonRenderer did not assign the expected native title payload.");

		var retainedPeer = RetainNativePeer(nativeButton);

		if (clearNativeTitleBeforeDispose)
			ClearNativeTitle(nativeButton);

		renderer.Dispose();
		button.Text = null;
		button.BindingContext = null;
		contextHandler.DisconnectHandler();

		retainedPeers.Add(retainedPeer);
		tracked.Add(TrackedCycle.Create(cycle, renderer, button));
	}

	static string CreateOperationalTitle(int cycle)
	{
		var header = $"Cycle {cycle:000} legacy workflow command. ";
		var sentence = "This action label was generated from localized workflow metadata, copied policy text, and offline task context. ";
		var targetChars = (int)(PayloadBytesPerButton / 2);
		var builder = new StringBuilder(targetChars + sentence.Length);
		builder.Append(header);

		while (builder.Length < targetChars)
			builder.Append(sentence);

		if (builder.Length > targetChars)
			builder.Length = targetChars;

		return builder.ToString();
	}

	static void SetRealisticBounds(UIView view, int width, int height)
	{
		var bounds = new CGRect(0, 0, width, height);
		view.Frame = bounds;
		view.Bounds = bounds;
	}

	static void StabilizeNativeLabelLayout(UIButton button)
	{
		if (button.TitleLabel is not { } titleLabel)
			return;

		titleLabel.Lines = 1;
		titleLabel.LineBreakMode = UILineBreakMode.TailTruncation;
		titleLabel.AdjustsFontSizeToFitWidth = false;
		titleLabel.Frame = new CGRect(0, 0, 720, 64);
	}

	static bool NativeTitleHasPayload(UIButton button) =>
		EstimateButtonTitleBytes(button) >= PayloadBytesPerButton * 0.95;

	static void ClearNativeTitle(UIButton button)
	{
		foreach (var state in TitleStates)
		{
			button.SetTitle(null, state);
			button.SetAttributedTitle(null, state);
		}

		if (button.TitleLabel is { } titleLabel)
		{
			titleLabel.Text = null;
			titleLabel.AttributedText = null;
		}
	}

	static RetainedButtonPeer RetainNativePeer(UIButton button)
	{
		var handle = button.Handle;
		if (handle == IntPtr.Zero)
			throw new InvalidOperationException("Cannot retain a native UIButton peer with a zero handle.");

		var retained = IntPtr_objc_msgSend(handle, RetainSelector);
		if (retained == IntPtr.Zero)
			throw new InvalidOperationException("Objective-C retain returned a zero handle.");

		return new RetainedButtonPeer(retained);
	}

	static NativeTitleSnapshot GetNativeTitleSnapshot(RetainedButtonPeer retainedPeer)
	{
		var peer = retainedPeer.TryGetPeer();
		if (peer is null)
			return new NativeTitleSnapshot(Alive: false, EstimatedTitleBytes: 0);

		return new NativeTitleSnapshot(Alive: true, EstimateButtonTitleBytes(peer));
	}

	static long EstimateButtonTitleBytes(UIButton button)
	{
		long maxStateBytes = 0;

		foreach (var state in TitleStates)
		{
			var stateBytes = EstimateTextBytes(
				button.Title(state),
				button.GetAttributedTitle(state)?.Value);
			maxStateBytes = Math.Max(maxStateBytes, stateBytes);
		}

		var labelBytes = button.TitleLabel is null
			? 0
			: EstimateTextBytes(button.TitleLabel.Text, button.TitleLabel.AttributedText?.Value);

		return Math.Max(maxStateBytes, labelBytes);
	}

	static long EstimateTextBytes(string? text, string? attributedText)
	{
		var retainedText = attributedText ?? text;
		return string.IsNullOrEmpty(retainedText) ? 0 : retainedText.Length * 2L;
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

	internal sealed record NativeTitleSnapshot(bool Alive, long EstimatedTitleBytes);

	internal sealed class RetainedButtonPeer
	{
		public RetainedButtonPeer(IntPtr handle)
		{
			Handle = handle;
		}

		public IntPtr Handle { get; }

		public UIButton? TryGetPeer()
		{
			if (Handle == IntPtr.Zero)
				return null;

			try
			{
				return Runtime.GetNSObject<UIButton>(Handle, false);
			}
			catch
			{
				return null;
			}
		}
	}

	internal sealed record TrackedCycle(
		int Cycle,
		WeakReference<object> Renderer,
		WeakReference<object> VirtualView)
	{
		public static TrackedCycle Create(int cycle, object renderer, object virtualView)
		{
			return new TrackedCycle(
				cycle,
				new WeakReference<object>(renderer),
				new WeakReference<object>(virtualView));
		}
	}

	internal sealed record ScenarioResult(
		string Name,
		int TrackedCycles,
		int RetainedNativePeers,
		int NativePeersWithTitle,
		long EstimatedNativeTitleBytes,
		int AliveRenderers,
		int AliveVirtualViews)
	{
		internal static ScenarioResult From(
			string name,
			IReadOnlyList<RetainedButtonPeer> retainedPeers,
			IReadOnlyList<TrackedCycle> tracked)
		{
			var retainedNativePeers = 0;
			var nativePeersWithTitle = 0;
			long estimatedNativeTitleBytes = 0;

			foreach (var retainedPeer in retainedPeers)
			{
				var snapshot = GetNativeTitleSnapshot(retainedPeer);
				if (!snapshot.Alive)
					continue;

				retainedNativePeers++;
				if (snapshot.EstimatedTitleBytes > 0)
				{
					nativePeersWithTitle++;
					estimatedNativeTitleBytes += Math.Min(snapshot.EstimatedTitleBytes, PayloadBytesPerButton);
				}
			}

			var aliveRenderers = 0;
			var aliveVirtualViews = 0;

			foreach (var cycle in tracked)
			{
				if (cycle.Renderer.TryGetTarget(out _))
					aliveRenderers++;

				if (cycle.VirtualView.TryGetTarget(out _))
					aliveVirtualViews++;
			}

			return new ScenarioResult(
				name,
				tracked.Count,
				retainedNativePeers,
				nativePeersWithTitle,
				estimatedNativeTitleBytes,
				aliveRenderers,
				aliveVirtualViews);
		}
	}
}

internal sealed record ReproReport(
	int Cycles,
	int PayloadKiBPerButton,
	long BaselineManagedBytes,
	long FinalManagedBytes,
	ReproSession.ScenarioResult Control,
	ReproSession.ScenarioResult Current)
{
	public bool LeakProved =>
		Control.RetainedNativePeers == Cycles &&
		Control.NativePeersWithTitle == 0 &&
		Control.AliveVirtualViews <= 1 &&
		Control.AliveRenderers <= 1 &&
		Current.RetainedNativePeers == Cycles &&
		Current.NativePeersWithTitle == Cycles &&
		Current.EstimatedNativeTitleBytes >= Cycles * PayloadKiBPerButton * 1024L * 0.95 &&
		Current.AliveVirtualViews <= 1 &&
		Current.AliveRenderers <= 1;

	public string ToText()
	{
		var heapDeltaMiB = (FinalManagedBytes - BaselineManagedBytes) / 1024d / 1024d;
		var controlMiB = Control.EstimatedNativeTitleBytes / 1024d / 1024d;
		var currentMiB = Current.EstimatedNativeTitleBytes / 1024d / 1024d;

		return string.Join(Environment.NewLine,
			"IosButtonRendererTitleRetentionRepro",
			$"Button renderer cycles per scenario: {Cycles}",
			$"Payload per native button title: {PayloadKiBPerButton} KiB",
			$"Baseline managed heap: {BaselineManagedBytes:N0} bytes",
			$"Final managed heap: {FinalManagedBytes:N0} bytes",
			$"Managed heap delta: {heapDeltaMiB:N1} MiB",
			$"Leak proved: {LeakProved}",
			string.Empty,
			Format(Control),
			string.Empty,
			Format(Current),
			string.Empty,
			$"Control estimated retained native title payload: {controlMiB:N1} MiB",
			$"Current estimated retained native title payload: {currentMiB:N1} MiB",
			$"RESULT: {(LeakProved ? "PROVEN" : "NOT PROVEN")}");
	}

	static string Format(ReproSession.ScenarioResult result)
	{
		var retainedMiB = result.EstimatedNativeTitleBytes / 1024d / 1024d;
		return string.Join(Environment.NewLine,
			$"Run: {result.Name}",
			$"  tracked cycles: {result.TrackedCycles}",
			$"  retained native UIButton peers: {result.RetainedNativePeers}/{result.TrackedCycles}",
			$"  native peers with assigned title: {result.NativePeersWithTitle}/{result.TrackedCycles}",
			$"  estimated retained native title bytes: {result.EstimatedNativeTitleBytes:N0}",
			$"  estimated retained native title MiB: {retainedMiB:N1}",
			$"  alive renderers: {result.AliveRenderers}/{result.TrackedCycles}",
			$"  alive virtual views: {result.AliveVirtualViews}/{result.TrackedCycles}");
	}
}

internal sealed class LeakProbeButtonRenderer : CompatibilityButtonRenderer, IImageVisualElementRenderer
{
	UIImageView IImageVisualElementRenderer.GetImage()
	{
		return null!;
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
