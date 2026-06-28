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

namespace IosLabelRendererNativeTextRetentionRepro;

internal static class ReproSession
{
	internal const int Cycles = 96;
	internal const int PayloadKiBPerLabel = 512;
	const long PayloadBytesPerLabel = PayloadKiBPerLabel * 1024L;

	static readonly IntPtr RetainSelector = Selector.GetHandle("retain");
	static readonly List<IReadOnlyList<RetainedLabelPeer>> RetainedNativeLabels = new();

	public static readonly string ResultsPath =
		Path.Combine("/tmp", "ios-labelrenderer-native-text-retention-results.txt");

	public static ReproReport Run(IMauiContext mauiContext)
	{
		WriteProgress("Starting iOS LabelRenderer native text retention repro.");
		ForceFullGc();
		var baselineBytes = GC.GetTotalMemory(true);

		WriteProgress("Running control scenario.");
		var control = RunScenario(
			"control: clear native UILabel text slots before renderer disposal",
			mauiContext,
			clearNativeTextBeforeDispose: true);

		WriteProgress("Running current MAUI scenario.");
		var current = RunScenario(
			"current: LabelRenderer disposal leaves native UILabel text assigned",
			mauiContext,
			clearNativeTextBeforeDispose: false);

		WriteProgress("Finalizing report.");
		ForceFullGc();
		var finalBytes = GC.GetTotalMemory(true);

		GC.KeepAlive(RetainedNativeLabels);

		return new ReproReport(Cycles, PayloadKiBPerLabel, baselineBytes, finalBytes, control, current);
	}

	static ScenarioResult RunScenario(
		string name,
		IMauiContext mauiContext,
		bool clearNativeTextBeforeDispose)
	{
		var retainedLabels = new List<RetainedLabelPeer>(Cycles);
		var tracked = new List<TrackedCycle>(Cycles);

		for (var i = 0; i < Cycles; i++)
		{
			if (i % 12 == 0)
				WriteProgress($"{name}: cycle {i}/{Cycles}");

			CreateLabelRendererCycle(i, mauiContext, retainedLabels, tracked, clearNativeTextBeforeDispose);
		}

		RetainedNativeLabels.Add(retainedLabels);
		ForceFullGc();

		return ScenarioResult.From(name, retainedLabels, tracked);
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	static void CreateLabelRendererCycle(
		int cycle,
		IMauiContext mauiContext,
		List<RetainedLabelPeer> retainedLabels,
		List<TrackedCycle> tracked,
		bool clearNativeTextBeforeDispose)
	{
		using var pool = new NSAutoreleasePool();

		var label = new Label
		{
			AutomationId = $"labelrenderer-native-text-{cycle:000}",
			Text = CreateDocumentText(cycle),
			LineBreakMode = LineBreakMode.WordWrap,
			CharacterSpacing = 0.1,
			WidthRequest = 720,
			HeightRequest = 180
		};
		label.Layout(new Rect(0, 0, 720, 180));

		var contextHandler = new ContextOnlyHandler(mauiContext);
		contextHandler.SetVirtualView(label);

		var renderer = new LabelRenderer();
		SetRealisticBounds(renderer);
		renderer.SetElement(label);
		renderer.LayoutSubviews();

		var nativeLabel = renderer.Control
			?? throw new InvalidOperationException("LabelRenderer did not create a native UILabel.");

		if (!NativeTextHasPayload(nativeLabel))
			throw new InvalidOperationException("LabelRenderer did not assign the expected native text payload.");

		var retainedLabel = RetainNativeLabel(nativeLabel);

		if (clearNativeTextBeforeDispose)
			ClearNativeText(nativeLabel);

		renderer.Dispose();
		label.Text = null;
		label.BindingContext = null;
		contextHandler.DisconnectHandler();

		retainedLabels.Add(retainedLabel);
		tracked.Add(TrackedCycle.Create(cycle, renderer, label));
	}

	static string CreateDocumentText(int cycle)
	{
		var header = $"Cycle {cycle:000} legacy label retained disclosure text. ";
		var sentence = "This status label contains copied claim notes, generated summaries, policy terms, and audit text. ";
		var targetChars = (int)(PayloadBytesPerLabel / 2);
		var builder = new StringBuilder(targetChars + sentence.Length);
		builder.Append(header);

		while (builder.Length < targetChars)
			builder.Append(sentence);

		if (builder.Length > targetChars)
			builder.Length = targetChars;

		return builder.ToString();
	}

	static void SetRealisticBounds(UIView renderer)
	{
		var bounds = new CGRect(0, 0, 720, 180);
		renderer.Frame = bounds;
		renderer.Bounds = bounds;
	}

	static bool NativeTextHasPayload(UILabel label) =>
		EstimateTextBytes(label.Text, label.AttributedText?.Value) >= PayloadBytesPerLabel * 0.95;

	static void ClearNativeText(UILabel label)
	{
		label.Text = null;
		label.AttributedText = null;
	}

	static RetainedLabelPeer RetainNativeLabel(UILabel label)
	{
		var handle = label.Handle;
		if (handle == IntPtr.Zero)
			throw new InvalidOperationException("Cannot retain a native UILabel with a zero handle.");

		var retained = IntPtr_objc_msgSend(handle, RetainSelector);
		if (retained == IntPtr.Zero)
			throw new InvalidOperationException("Objective-C retain returned a zero handle.");

		return new RetainedLabelPeer(retained);
	}

	static NativeTextSnapshot GetNativeTextSnapshot(RetainedLabelPeer retainedLabel)
	{
		var label = retainedLabel.TryGetLabel();
		if (label is null)
			return new NativeTextSnapshot(Alive: false, EstimatedTextBytes: 0);

		return new NativeTextSnapshot(
			Alive: true,
			EstimatedTextBytes: EstimateTextBytes(label.Text, label.AttributedText?.Value));
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

	internal sealed record NativeTextSnapshot(bool Alive, long EstimatedTextBytes);

	internal sealed class RetainedLabelPeer
	{
		public RetainedLabelPeer(IntPtr handle)
		{
			Handle = handle;
		}

		public IntPtr Handle { get; }

		public UILabel? TryGetLabel()
		{
			if (Handle == IntPtr.Zero)
				return null;

			try
			{
				return Runtime.GetNSObject<UILabel>(Handle, false);
			}
			catch
			{
				return null;
			}
		}
	}

	internal sealed record TrackedCycle(
		int Cycle,
		WeakReference<LabelRenderer> Renderer,
		WeakReference<Label> Label)
	{
		public static TrackedCycle Create(int cycle, LabelRenderer renderer, Label label)
		{
			return new TrackedCycle(
				cycle,
				new WeakReference<LabelRenderer>(renderer),
				new WeakReference<Label>(label));
		}
	}

	internal sealed record ScenarioResult(
		string Name,
		int TrackedCycles,
		int RetainedNativeLabels,
		int NativeLabelsWithText,
		long EstimatedNativeTextBytes,
		int AliveRenderers,
		int AliveLabels)
	{
		internal static ScenarioResult From(
			string name,
			IReadOnlyList<RetainedLabelPeer> retainedLabels,
			IReadOnlyList<TrackedCycle> tracked)
		{
			var retainedNativeLabels = 0;
			var nativeLabelsWithText = 0;
			long estimatedNativeTextBytes = 0;

			foreach (var retainedLabel in retainedLabels)
			{
				var snapshot = GetNativeTextSnapshot(retainedLabel);
				if (!snapshot.Alive)
					continue;

				retainedNativeLabels++;
				if (snapshot.EstimatedTextBytes > 0)
				{
					nativeLabelsWithText++;
					estimatedNativeTextBytes += Math.Min(snapshot.EstimatedTextBytes, PayloadBytesPerLabel);
				}
			}

			var aliveRenderers = 0;
			var aliveLabels = 0;

			foreach (var cycle in tracked)
			{
				if (cycle.Renderer.TryGetTarget(out _))
					aliveRenderers++;

				if (cycle.Label.TryGetTarget(out _))
					aliveLabels++;
			}

			return new ScenarioResult(
				name,
				tracked.Count,
				retainedNativeLabels,
				nativeLabelsWithText,
				estimatedNativeTextBytes,
				aliveRenderers,
				aliveLabels);
		}
	}
}

internal sealed record ReproReport(
	int Cycles,
	int PayloadKiBPerLabel,
	long BaselineManagedBytes,
	long FinalManagedBytes,
	ReproSession.ScenarioResult Control,
	ReproSession.ScenarioResult Current)
{
	public bool LeakProved =>
		Control.RetainedNativeLabels == Cycles &&
		Control.NativeLabelsWithText == 0 &&
		Control.AliveLabels == 0 &&
		Current.RetainedNativeLabels == Cycles &&
		Current.NativeLabelsWithText == Cycles &&
		Current.EstimatedNativeTextBytes >= Cycles * PayloadKiBPerLabel * 1024L * 0.95 &&
		Current.AliveLabels == 0;

	public string ToText()
	{
		var heapDeltaMiB = (FinalManagedBytes - BaselineManagedBytes) / 1024d / 1024d;
		var controlMiB = Control.EstimatedNativeTextBytes / 1024d / 1024d;
		var currentMiB = Current.EstimatedNativeTextBytes / 1024d / 1024d;

		return string.Join(Environment.NewLine,
			"IosLabelRendererNativeTextRetentionRepro",
			$"Cycles: {Cycles}",
			$"Payload per native label: {PayloadKiBPerLabel} KiB",
			$"Baseline managed heap: {BaselineManagedBytes:N0} bytes",
			$"Final managed heap: {FinalManagedBytes:N0} bytes",
			$"Managed heap delta: {heapDeltaMiB:N1} MiB",
			$"Leak proved: {LeakProved}",
			string.Empty,
			Format(Control),
			string.Empty,
			Format(Current),
			string.Empty,
			$"Control estimated retained native text payload: {controlMiB:N1} MiB",
			$"Current estimated retained native text payload: {currentMiB:N1} MiB",
			$"RESULT: {(LeakProved ? "PROVEN" : "NOT PROVEN")}");
	}

	static string Format(ReproSession.ScenarioResult result)
	{
		var retainedMiB = result.EstimatedNativeTextBytes / 1024d / 1024d;

		return string.Join(Environment.NewLine,
			$"Run: {result.Name}",
			$"  tracked cycles: {result.TrackedCycles}",
			$"  retained native UILabel peers: {result.RetainedNativeLabels}/{result.TrackedCycles}",
			$"  native labels with assigned text: {result.NativeLabelsWithText}/{result.TrackedCycles}",
			$"  estimated retained native text bytes: {result.EstimatedNativeTextBytes:N0}",
			$"  estimated retained native text MiB: {retainedMiB:N1}",
			$"  alive renderers: {result.AliveRenderers}/{result.TrackedCycles}",
			$"  alive Labels: {result.AliveLabels}/{result.TrackedCycles}");
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
