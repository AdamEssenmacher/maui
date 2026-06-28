#nullable enable

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using CoreGraphics;
using Foundation;
using Microsoft.Maui.Handlers;
using Microsoft.Maui.Platform;
using ObjCRuntime;
using UIKit;

namespace IosSwipeItemTitleRetentionRepro;

internal static class ReproSession
{
	internal const int Cycles = 96;
	internal const int PayloadKiBPerButton = 256;
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
		Path.Combine("/tmp", "ios-swipeitem-title-retention-results.txt");

	public static ReproReport Run(IMauiContext mauiContext)
	{
		WriteProgress("Starting iOS SwipeItem title retention repro.");
		ForceFullGc();
		var baselineBytes = GC.GetTotalMemory(true);

		WriteProgress("Running control scenario.");
		var control = RunScenario(
			"control: clear native swipe item UIButton text slots after handler disconnect",
			mauiContext,
			clearNativeTextAfterDisconnect: true);

		WriteProgress("Running current MAUI scenario.");
		var current = RunScenario(
			"current: handler disconnect leaves native swipe item UIButton text slots assigned",
			mauiContext,
			clearNativeTextAfterDisconnect: false);

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
		bool clearNativeTextAfterDisconnect)
	{
		var retainedPeers = new List<RetainedButtonPeer>(Cycles);
		var tracked = new List<TrackedCycle>(Cycles);

		for (var i = 0; i < Cycles; i++)
		{
			if (i % 12 == 0)
				WriteProgress($"{name}: cycle {i}/{Cycles}");

			CreateSwipeItemCycle(i, mauiContext, retainedPeers, tracked, clearNativeTextAfterDisconnect);
		}

		RetainedNativePeers.Add(retainedPeers);
		ForceFullGc();

		return ScenarioResult.From(name, retainedPeers, tracked);
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	static void CreateSwipeItemCycle(
		int cycle,
		IMauiContext mauiContext,
		List<RetainedButtonPeer> retainedPeers,
		List<TrackedCycle> tracked,
		bool clearNativeTextAfterDisconnect)
	{
		using var pool = new NSAutoreleasePool();

		var swipeItem = new SwipeItem
		{
			AutomationId = $"swipeitem-title-{cycle:000}",
			Text = CreateOperationalTitle(cycle),
			BackgroundColor = Color.FromRgb((cycle * 37) % 255, 64, 132),
			Command = new Command(() => { })
		};

		var handler = new SwipeItemMenuItemHandler();
		((IElementHandler)handler).SetMauiContext(mauiContext);
		((IElementHandler)handler).SetVirtualView(swipeItem);

		var nativeButton = handler.PlatformView
			?? throw new InvalidOperationException("SwipeItemMenuItemHandler did not create a native UIButton.");
		SetRealisticBounds(nativeButton, 720, 72);
		handler.UpdateValue(nameof(ISwipeItemMenuItem.Text));

		if (!NativeTextHasPayload(nativeButton))
			throw new InvalidOperationException("SwipeItemMenuItemHandler did not assign the expected native text payload.");

		var retainedPeer = RetainNativePeer(nativeButton);

		((IElementHandler)handler).DisconnectHandler();
		swipeItem.Text = null;
		swipeItem.BindingContext = null;

		if (clearNativeTextAfterDisconnect)
			ClearNativeText(nativeButton);

		retainedPeers.Add(retainedPeer);
		tracked.Add(TrackedCycle.Create(cycle, handler, swipeItem));
	}

	static string CreateOperationalTitle(int cycle)
	{
		var header = $"Cycle {cycle:000} imported swipe workflow command. ";
		var sentence = "This swipe action label was generated from localized workflow metadata, copied policy text, and offline task context. ";
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

	static bool NativeTextHasPayload(UIButton button) =>
		EstimateButtonTextBytes(button) >= PayloadBytesPerButton * 0.95;

	static void ClearNativeText(UIButton button)
	{
		button.RestorationIdentifier = null;

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

	static NativeTextSnapshot GetNativeTextSnapshot(RetainedButtonPeer retainedPeer)
	{
		var peer = retainedPeer.TryGetPeer();
		if (peer is null)
			return new NativeTextSnapshot(Alive: false, EstimatedTextBytes: 0);

		return new NativeTextSnapshot(Alive: true, EstimateButtonTextBytes(peer));
	}

	static long EstimateButtonTextBytes(UIButton button)
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

		var restorationBytes = EstimateTextBytes(button.RestorationIdentifier, null);

		return Math.Max(Math.Max(maxStateBytes, labelBytes), restorationBytes);
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
		WeakReference<SwipeItemMenuItemHandler> Handler,
		WeakReference<SwipeItem> SwipeItem)
	{
		public static TrackedCycle Create(
			int cycle,
			SwipeItemMenuItemHandler handler,
			SwipeItem swipeItem)
		{
			return new TrackedCycle(
				cycle,
				new WeakReference<SwipeItemMenuItemHandler>(handler),
				new WeakReference<SwipeItem>(swipeItem));
		}
	}

	internal sealed record ScenarioResult(
		string Name,
		int TrackedCycles,
		int RetainedNativeButtons,
		int NativeButtonsWithText,
		long EstimatedNativeTextBytes,
		int AliveHandlers,
		int AliveSwipeItems)
	{
		internal static ScenarioResult From(
			string name,
			IReadOnlyList<RetainedButtonPeer> retainedPeers,
			IReadOnlyList<TrackedCycle> tracked)
		{
			var retainedNativeButtons = 0;
			var nativeButtonsWithText = 0;
			long estimatedNativeTextBytes = 0;

			foreach (var retainedPeer in retainedPeers)
			{
				var snapshot = GetNativeTextSnapshot(retainedPeer);
				if (!snapshot.Alive)
					continue;

				retainedNativeButtons++;
				if (snapshot.EstimatedTextBytes > 0)
				{
					nativeButtonsWithText++;
					estimatedNativeTextBytes += Math.Min(snapshot.EstimatedTextBytes, PayloadBytesPerButton);
				}
			}

			var aliveHandlers = 0;
			var aliveSwipeItems = 0;

			foreach (var cycle in tracked)
			{
				if (cycle.Handler.TryGetTarget(out _))
					aliveHandlers++;

				if (cycle.SwipeItem.TryGetTarget(out _))
					aliveSwipeItems++;
			}

			return new ScenarioResult(
				name,
				tracked.Count,
				retainedNativeButtons,
				nativeButtonsWithText,
				estimatedNativeTextBytes,
				aliveHandlers,
				aliveSwipeItems);
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
		Control.RetainedNativeButtons == Cycles &&
		Control.NativeButtonsWithText == 0 &&
		Control.AliveHandlers <= 1 &&
		Control.AliveSwipeItems <= 1 &&
		Current.RetainedNativeButtons == Cycles &&
		Current.NativeButtonsWithText == Cycles &&
		Current.EstimatedNativeTextBytes >= Cycles * PayloadKiBPerButton * 1024L * 0.95 &&
		Current.AliveHandlers <= 1 &&
		Current.AliveSwipeItems <= 1;

	public string ToText()
	{
		var heapDeltaMiB = (FinalManagedBytes - BaselineManagedBytes) / 1024d / 1024d;
		var controlMiB = Control.EstimatedNativeTextBytes / 1024d / 1024d;
		var currentMiB = Current.EstimatedNativeTextBytes / 1024d / 1024d;

		return string.Join(Environment.NewLine,
			"IosSwipeItemTitleRetentionRepro",
			$"SwipeItem cycles per scenario: {Cycles}",
			$"Payload per native swipe item title: {PayloadKiBPerButton} KiB",
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
			$"  retained native UIButton peers: {result.RetainedNativeButtons}/{result.TrackedCycles}",
			$"  native buttons with assigned text: {result.NativeButtonsWithText}/{result.TrackedCycles}",
			$"  estimated retained native text bytes: {result.EstimatedNativeTextBytes:N0}",
			$"  estimated retained native text MiB: {retainedMiB:N1}",
			$"  alive handlers: {result.AliveHandlers}/{result.TrackedCycles}",
			$"  alive SwipeItems: {result.AliveSwipeItems}/{result.TrackedCycles}");
	}
}
