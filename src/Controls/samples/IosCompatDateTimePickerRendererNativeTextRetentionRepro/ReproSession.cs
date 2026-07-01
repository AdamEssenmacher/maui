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
using CompatibilityDatePickerRenderer = Microsoft.Maui.Controls.Compatibility.Platform.iOS.DatePickerRenderer;
using CompatibilityTimePickerRenderer = Microsoft.Maui.Controls.Compatibility.Platform.iOS.TimePickerRenderer;

namespace IosCompatDateTimePickerRendererNativeTextRetentionRepro;

internal static class ReproSession
{
	internal const int CyclesPerRendererType = 128;
	internal const int PayloadKiBPerControl = 8;
	const int RendererTypeCount = 2;
	const long PayloadBytesPerControl = PayloadKiBPerControl * 1024L;

	static readonly IntPtr RetainSelector = Selector.GetHandle("retain");
	static readonly List<IReadOnlyList<RetainedTextPeer>> RetainedNativePeers = new();

	public static readonly string ResultsPath =
		Path.Combine("/tmp", "ios-compat-datetimepicker-renderer-native-text-retention-results.txt");

	public static ReproReport Run(IMauiContext mauiContext)
	{
		WriteProgress("Starting iOS compatibility DatePickerRenderer/TimePickerRenderer native text retention repro.");
		ForceFullGc();
		var baselineBytes = GC.GetTotalMemory(true);

		WriteProgress("Running control scenario.");
		var control = RunScenario(
			"control: clear native text and input slots before renderer disposal",
			mauiContext,
			clearNativeStateBeforeDispose: true);

		WriteProgress("Running current MAUI scenario.");
		var current = RunScenario(
			"current: renderer disposal leaves native text and input slots assigned",
			mauiContext,
			clearNativeStateBeforeDispose: false);

		WriteProgress("Finalizing report.");
		ForceFullGc();
		var finalBytes = GC.GetTotalMemory(true);

		GC.KeepAlive(RetainedNativePeers);

		return new ReproReport(
			CyclesPerRendererType,
			PayloadKiBPerControl,
			baselineBytes,
			finalBytes,
			control,
			current);
	}

	static ScenarioResult RunScenario(
		string name,
		IMauiContext mauiContext,
		bool clearNativeStateBeforeDispose)
	{
		var retainedPeers = new List<RetainedTextPeer>(CyclesPerRendererType * RendererTypeCount);
		var tracked = new List<TrackedCycle>(CyclesPerRendererType * RendererTypeCount);

		for (var i = 0; i < CyclesPerRendererType; i++)
		{
			if (i % 32 == 0)
				WriteProgress($"{name}: cycle {i}/{CyclesPerRendererType}");

			CreateDatePickerRendererCycle(i, mauiContext, retainedPeers, tracked, clearNativeStateBeforeDispose);
			CreateTimePickerRendererCycle(i, mauiContext, retainedPeers, tracked, clearNativeStateBeforeDispose);
		}

		RetainedNativePeers.Add(retainedPeers);
		ForceFullGc();

		return ScenarioResult.From(name, retainedPeers, tracked);
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	static void CreateDatePickerRendererCycle(
		int cycle,
		IMauiContext mauiContext,
		List<RetainedTextPeer> retainedPeers,
		List<TrackedCycle> tracked,
		bool clearNativeStateBeforeDispose)
	{
		using var pool = new NSAutoreleasePool();

		var datePicker = new DatePicker
		{
			AutomationId = $"compat-date-picker-renderer-text-{cycle:000}",
			Date = new DateTime(2026, 7, 1).AddDays(cycle % 28),
			Format = CreateDateFormat(cycle),
			CharacterSpacing = 0.1,
			WidthRequest = 720,
			HeightRequest = 48
		};
		datePicker.Layout(new Rect(0, 0, 720, 48));

		var contextHandler = new ContextOnlyHandler(mauiContext);
		contextHandler.SetVirtualView(datePicker);

		var renderer = new CompatibilityDatePickerRenderer();
		SetRealisticBounds(renderer, 720, 48);
		renderer.SetElement(datePicker);

		var nativeTextField = renderer.Control
			?? throw new InvalidOperationException("DatePickerRenderer did not create a native UITextField.");
		nativeTextField.Frame = new CGRect(0, 0, 720, 48);

		if (!NativeTextHasPayload(nativeTextField))
			throw new InvalidOperationException("DatePickerRenderer did not assign the expected native text payload.");

		var retainedPeer = RetainNativePeer("DatePickerRenderer", nativeTextField);

		if (clearNativeStateBeforeDispose)
			ClearNativeState(nativeTextField);

		renderer.Dispose();
		datePicker.Format = "d";
		datePicker.BindingContext = null;
		contextHandler.DisconnectHandler();

		retainedPeers.Add(retainedPeer);
		tracked.Add(TrackedCycle.Create("DatePickerRenderer", cycle, renderer, datePicker));
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	static void CreateTimePickerRendererCycle(
		int cycle,
		IMauiContext mauiContext,
		List<RetainedTextPeer> retainedPeers,
		List<TrackedCycle> tracked,
		bool clearNativeStateBeforeDispose)
	{
		using var pool = new NSAutoreleasePool();

		var timePicker = new TimePicker
		{
			AutomationId = $"compat-time-picker-renderer-text-{cycle:000}",
			Time = new TimeSpan(8 + (cycle % 10), cycle % 60, 0),
			Format = CreateTimeFormat(cycle),
			CharacterSpacing = 0.1,
			WidthRequest = 720,
			HeightRequest = 48
		};
		timePicker.Layout(new Rect(0, 0, 720, 48));

		var contextHandler = new ContextOnlyHandler(mauiContext);
		contextHandler.SetVirtualView(timePicker);

		var renderer = new CompatibilityTimePickerRenderer();
		SetRealisticBounds(renderer, 720, 48);
		renderer.SetElement(timePicker);

		var nativeTextField = renderer.Control
			?? throw new InvalidOperationException("TimePickerRenderer did not create a native UITextField.");
		nativeTextField.Frame = new CGRect(0, 0, 720, 48);

		if (!NativeTextHasPayload(nativeTextField))
			throw new InvalidOperationException("TimePickerRenderer did not assign the expected native text payload.");

		var retainedPeer = RetainNativePeer("TimePickerRenderer", nativeTextField);

		if (clearNativeStateBeforeDispose)
			ClearNativeState(nativeTextField);

		renderer.Dispose();
		timePicker.Format = "t";
		timePicker.BindingContext = null;
		contextHandler.DisconnectHandler();

		retainedPeers.Add(retainedPeer);
		tracked.Add(TrackedCycle.Create("TimePickerRenderer", cycle, renderer, timePicker));
	}

	static string CreateDateFormat(int cycle)
	{
		return "yyyy-MM-dd " + CreateLiteralPayload("route schedule date", cycle);
	}

	static string CreateTimeFormat(int cycle)
	{
		return "HH:mm " + CreateLiteralPayload("dispatch window time", cycle);
	}

	static string CreateLiteralPayload(string label, int cycle)
	{
		var targetChars = (int)(PayloadBytesPerControl / 2);
		var sentence = $"{label} cycle {cycle:0000} imported calendar payload with SLA notes and localized shift metadata. ";
		var builder = new StringBuilder(targetChars + 32);
		builder.Append('\'');

		while (builder.Length < targetChars)
			builder.Append(sentence);

		if (builder.Length > targetChars)
			builder.Length = targetChars;

		builder.Append('\'');
		return builder.ToString();
	}

	static void SetRealisticBounds(UIView view, int width, int height)
	{
		var bounds = new CGRect(0, 0, width, height);
		view.Frame = bounds;
		view.Bounds = bounds;
	}

	static bool NativeTextHasPayload(UITextField textField) =>
		EstimateTextBytes(textField.Text, textField.AttributedText?.Value) >= PayloadBytesPerControl * 0.95;

	static void ClearNativeState(UITextField textField)
	{
		textField.Text = null;
		textField.AttributedText = null;
		textField.Placeholder = null;
		textField.AttributedPlaceholder = null;
		textField.InputView = null;
		textField.InputAccessoryView = null;
	}

	static RetainedTextPeer RetainNativePeer(string controlType, NSObject peer)
	{
		var handle = peer.Handle;
		if (handle == IntPtr.Zero)
			throw new InvalidOperationException($"Cannot retain a native {controlType} peer with a zero handle.");

		var retained = IntPtr_objc_msgSend(handle, RetainSelector);
		if (retained == IntPtr.Zero)
			throw new InvalidOperationException("Objective-C retain returned a zero handle.");

		return new RetainedTextPeer(controlType, retained);
	}

	static NativeTextSnapshot GetNativeTextSnapshot(RetainedTextPeer retainedPeer)
	{
		var peer = retainedPeer.TryGetPeer();
		return peer switch
		{
			UITextField textField => new NativeTextSnapshot(
				Alive: true,
				EstimateTextBytes(textField.Text, textField.AttributedText?.Value) +
					EstimateTextBytes(textField.Placeholder, textField.AttributedPlaceholder?.Value)),
			_ => new NativeTextSnapshot(Alive: false, EstimatedTextBytes: 0)
		};
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

	internal sealed class RetainedTextPeer
	{
		public RetainedTextPeer(string controlType, IntPtr handle)
		{
			ControlType = controlType;
			Handle = handle;
		}

		public string ControlType { get; }

		public IntPtr Handle { get; }

		public NSObject? TryGetPeer()
		{
			if (Handle == IntPtr.Zero)
				return null;

			try
			{
				return Runtime.GetNSObject<UITextField>(Handle, false);
			}
			catch
			{
				return null;
			}
		}
	}

	internal sealed record TrackedCycle(
		string ControlType,
		int Cycle,
		WeakReference<object> Renderer,
		WeakReference<object> VirtualView)
	{
		public static TrackedCycle Create(string controlType, int cycle, object renderer, object virtualView)
		{
			return new TrackedCycle(
				controlType,
				cycle,
				new WeakReference<object>(renderer),
				new WeakReference<object>(virtualView));
		}
	}

	internal sealed record TypeResult(
		int Tracked,
		int RetainedNativePeers,
		int NativePeersWithText,
		long EstimatedNativeTextBytes);

	sealed class TypeCounter
	{
		public int Tracked { get; set; }
		public int RetainedNativePeers { get; set; }
		public int NativePeersWithText { get; set; }
		public long EstimatedNativeTextBytes { get; set; }

		public TypeResult ToResult() =>
			new(Tracked, RetainedNativePeers, NativePeersWithText, EstimatedNativeTextBytes);
	}

	internal sealed record ScenarioResult(
		string Name,
		int TrackedCycles,
		int RetainedNativePeers,
		int NativePeersWithText,
		long EstimatedNativeTextBytes,
		int AliveRenderers,
		int AliveVirtualViews,
		IReadOnlyDictionary<string, TypeResult> ByControlType)
	{
		internal static ScenarioResult From(
			string name,
			IReadOnlyList<RetainedTextPeer> retainedPeers,
			IReadOnlyList<TrackedCycle> tracked)
		{
			var nativePeersWithText = 0;
			long estimatedNativeTextBytes = 0;
			var byType = new Dictionary<string, TypeCounter>(StringComparer.Ordinal);

			foreach (var retainedPeer in retainedPeers)
			{
				var counter = GetCounter(byType, retainedPeer.ControlType);
				counter.Tracked++;

				var snapshot = GetNativeTextSnapshot(retainedPeer);
				if (!snapshot.Alive)
					continue;

				counter.RetainedNativePeers++;
				if (snapshot.EstimatedTextBytes > 0)
				{
					nativePeersWithText++;
					estimatedNativeTextBytes += Math.Min(snapshot.EstimatedTextBytes, PayloadBytesPerControl);
					counter.NativePeersWithText++;
					counter.EstimatedNativeTextBytes += Math.Min(snapshot.EstimatedTextBytes, PayloadBytesPerControl);
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
				byType.Values.Sum(counter => counter.RetainedNativePeers),
				nativePeersWithText,
				estimatedNativeTextBytes,
				aliveRenderers,
				aliveVirtualViews,
				byType.ToDictionary(pair => pair.Key, pair => pair.Value.ToResult(), StringComparer.Ordinal));
		}

		static TypeCounter GetCounter(Dictionary<string, TypeCounter> values, string controlType)
		{
			if (!values.TryGetValue(controlType, out var counter))
			{
				counter = new TypeCounter();
				values.Add(controlType, counter);
			}

			return counter;
		}
	}
}

internal sealed record ReproReport(
	int CyclesPerRendererType,
	int PayloadKiBPerControl,
	long BaselineManagedBytes,
	long FinalManagedBytes,
	ReproSession.ScenarioResult Control,
	ReproSession.ScenarioResult Current)
{
	int TotalCycles => CyclesPerRendererType * 2;

	public bool LeakProved =>
		Control.RetainedNativePeers == TotalCycles &&
		Control.NativePeersWithText == 0 &&
		Control.AliveVirtualViews <= Control.ByControlType.Count &&
		Current.RetainedNativePeers == TotalCycles &&
		Current.NativePeersWithText == TotalCycles &&
		Current.EstimatedNativeTextBytes >= TotalCycles * PayloadKiBPerControl * 1024L * 0.95 &&
		Current.AliveVirtualViews <= Current.ByControlType.Count &&
		Current.ByControlType.TryGetValue("DatePickerRenderer", out var datePicker) &&
		datePicker.NativePeersWithText == CyclesPerRendererType &&
		Current.ByControlType.TryGetValue("TimePickerRenderer", out var timePicker) &&
		timePicker.NativePeersWithText == CyclesPerRendererType;

	public string ToText()
	{
		var heapDeltaMiB = (FinalManagedBytes - BaselineManagedBytes) / 1024d / 1024d;
		var controlMiB = Control.EstimatedNativeTextBytes / 1024d / 1024d;
		var currentMiB = Current.EstimatedNativeTextBytes / 1024d / 1024d;

		return string.Join(Environment.NewLine,
			"IosCompatDateTimePickerRendererNativeTextRetentionRepro",
			$"Cycles per renderer type: {CyclesPerRendererType}",
			$"Total renderer cycles per scenario: {TotalCycles}",
			$"Payload per native text control: {PayloadKiBPerControl} KiB",
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
		var lines = new List<string>
		{
			$"Run: {result.Name}",
			$"  tracked cycles: {result.TrackedCycles}",
			$"  retained native text peers: {result.RetainedNativePeers}/{result.TrackedCycles}",
			$"  native peers with assigned text: {result.NativePeersWithText}/{result.TrackedCycles}",
			$"  estimated retained native text bytes: {result.EstimatedNativeTextBytes:N0}",
			$"  estimated retained native text MiB: {retainedMiB:N1}",
			$"  alive renderers: {result.AliveRenderers}/{result.TrackedCycles}",
			$"  alive virtual views: {result.AliveVirtualViews}/{result.TrackedCycles}"
		};

		foreach (var pair in result.ByControlType.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
		{
			var value = pair.Value;
			lines.Add(
				$"  {pair.Key}: native={value.RetainedNativePeers}/{value.Tracked}, text={value.NativePeersWithText}/{value.Tracked}, estimatedBytes={value.EstimatedNativeTextBytes:N0}");
		}

		return string.Join(Environment.NewLine, lines);
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
