#nullable enable

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using CoreGraphics;
using Foundation;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Handlers;
using Microsoft.Maui.Platform;
using ObjCRuntime;
using UIKit;
using MauiRadioButton = Microsoft.Maui.Controls.RadioButton;
using MauiSwipeItemView = Microsoft.Maui.Controls.SwipeItemView;
using PlatformContentView = Microsoft.Maui.Platform.ContentView;

namespace IosContentBackedHandlerNativeSubviewRetentionRepro;

internal static class ReproSession
{
	internal const int CyclesPerHandlerType = 64;
	internal const int PayloadKiBPerChildLabel = 256;
	const long PayloadBytesPerChildLabel = PayloadKiBPerChildLabel * 1024L;

	static readonly IntPtr RetainSelector = Selector.GetHandle("retain");
	static readonly string[] HandlerKinds = ["RadioButtonHandler", "SwipeItemViewHandler"];
	static readonly List<IReadOnlyList<RetainedParentPeer>> RetainedNativeParents = new();

	public static readonly string ResultsPath =
		Path.Combine("/tmp", "ios-content-backed-handler-native-subview-retention-results.txt");

	public static ReproReport Run(IMauiContext mauiContext)
	{
		WriteProgress("Starting iOS/Mac Catalyst content-backed handler native subview retention repro.");
		ForceFullGc();
		var baselineBytes = GC.GetTotalMemory(true);

		WriteProgress("Running control scenario.");
		var control = RunScenario(
			"control: retained native ContentView parents after explicit ClearSubviews cleanup",
			mauiContext,
			clearSubviewsAfterDisconnect: true);

		WriteProgress("Running current MAUI scenario.");
		var current = RunScenario(
			"current: handler disconnect leaves current native child subview attached",
			mauiContext,
			clearSubviewsAfterDisconnect: false);

		WriteProgress("Finalizing report.");
		ForceFullGc();
		var finalBytes = GC.GetTotalMemory(true);

		GC.KeepAlive(RetainedNativeParents);

		return new ReproReport(
			CyclesPerHandlerType,
			HandlerKinds.Length,
			PayloadKiBPerChildLabel,
			baselineBytes,
			finalBytes,
			control,
			current);
	}

	static ScenarioResult RunScenario(
		string name,
		IMauiContext mauiContext,
		bool clearSubviewsAfterDisconnect)
	{
		var retainedParents = new List<RetainedParentPeer>(CyclesPerHandlerType * HandlerKinds.Length);
		var tracked = new List<TrackedCycle>(CyclesPerHandlerType * HandlerKinds.Length);

		for (var i = 0; i < CyclesPerHandlerType; i++)
		{
			if (i % 16 == 0)
				WriteProgress($"{name}: cycle {i}/{CyclesPerHandlerType}");

			CreateRadioButtonCycle(i, mauiContext, retainedParents, tracked, clearSubviewsAfterDisconnect);
			CreateSwipeItemViewCycle(i, mauiContext, retainedParents, tracked, clearSubviewsAfterDisconnect);
		}

		RetainedNativeParents.Add(retainedParents);
		ForceFullGc();

		return ScenarioResult.From(name, HandlerKinds, retainedParents, tracked);
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	static void CreateRadioButtonCycle(
		int cycle,
		IMauiContext mauiContext,
		List<RetainedParentPeer> retainedParents,
		List<TrackedCycle> tracked,
		bool clearSubviewsAfterDisconnect)
	{
		using var pool = new NSAutoreleasePool();

		var child = CreatePayloadLabel("radiobutton", cycle);
		var parent = new MauiRadioButton
		{
			AutomationId = $"retained-native-parent-radiobutton-{cycle:000}",
			Content = child,
			GroupName = $"retention-group-{cycle:000}",
			WidthRequest = 900,
			HeightRequest = 56
		};

		parent.Layout(new Rect(0, 0, 900, 56));
		child.Layout(new Rect(0, 0, 820, 40));

		var handler = (RadioButtonHandler)parent.ToHandler(mauiContext);
		var platformParent = handler.PlatformView;
		SetRealisticBounds(platformParent, 900, 56);
		handler.UpdateValue(nameof(IRadioButton.Content));

		var nativeLabel = FindPayloadLabel(platformParent)
			?? throw new InvalidOperationException("RadioButtonHandler did not attach a native UILabel child with the expected payload.");

		var childHandler = child.Handler
			?? throw new InvalidOperationException("RadioButton content Label did not receive a handler.");

		var retainedParent = RetainNativeParent("RadioButtonHandler", platformParent);

		((IElementHandler)handler).DisconnectHandler();

		if (clearSubviewsAfterDisconnect)
			platformParent.ClearSubviews();

		parent.BindingContext = null;
		child.BindingContext = null;

		retainedParents.Add(retainedParent);
		tracked.Add(TrackedCycle.Create("RadioButtonHandler", parent, handler, child, childHandler, nativeLabel));
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	static void CreateSwipeItemViewCycle(
		int cycle,
		IMauiContext mauiContext,
		List<RetainedParentPeer> retainedParents,
		List<TrackedCycle> tracked,
		bool clearSubviewsAfterDisconnect)
	{
		using var pool = new NSAutoreleasePool();

		var child = CreatePayloadLabel("swipeitemview", cycle);
		var parent = new MauiSwipeItemView
		{
			AutomationId = $"retained-native-parent-swipeitemview-{cycle:000}",
			Content = child,
			WidthRequest = 900,
			HeightRequest = 56
		};

		parent.Layout(new Rect(0, 0, 900, 56));
		child.Layout(new Rect(0, 0, 900, 40));

		var handler = (SwipeItemViewHandler)parent.ToHandler(mauiContext);
		var platformParent = handler.PlatformView;
		SetRealisticBounds(platformParent, 900, 56);
		handler.UpdateValue(nameof(ISwipeItemView.Content));

		var nativeLabel = FindPayloadLabel(platformParent)
			?? throw new InvalidOperationException("SwipeItemViewHandler did not attach a native UILabel child with the expected payload.");

		var childHandler = child.Handler
			?? throw new InvalidOperationException("SwipeItemView content Label did not receive a handler.");

		var retainedParent = RetainNativeParent("SwipeItemViewHandler", platformParent);

		((IElementHandler)handler).DisconnectHandler();

		if (clearSubviewsAfterDisconnect)
			platformParent.ClearSubviews();

		parent.BindingContext = null;
		child.BindingContext = null;

		retainedParents.Add(retainedParent);
		tracked.Add(TrackedCycle.Create("SwipeItemViewHandler", parent, handler, child, childHandler, nativeLabel));
	}

	static Label CreatePayloadLabel(string source, int cycle)
	{
		return new Label
		{
			AutomationId = $"{source}-payload-label-{cycle:000}",
			Text = CreatePayload(source, cycle),
			LineBreakMode = LineBreakMode.NoWrap,
			WidthRequest = 900,
			HeightRequest = 40
		};
	}

	static string CreatePayload(string source, int cycle)
	{
		var targetChars = (int)(PayloadBytesPerChildLabel / 2);
		var sentence =
			$"generated {source} approval row {cycle:0000} with entitlement notes, offline validation history, localized status, and audit metadata. ";
		var builder = new StringBuilder(targetChars + 32);

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

	static UILabel? FindPayloadLabel(UIView view)
	{
		foreach (var subview in view.Subviews)
		{
			if (subview is UILabel label && EstimateTextBytes(label.Text, label.AttributedText?.Value) >= PayloadBytesPerChildLabel * 0.95)
				return label;

			var nested = FindPayloadLabel(subview);
			if (nested is not null)
				return nested;
		}

		return null;
	}

	static int CountPayloadLabels(UIView view, out long estimatedBytes)
	{
		var count = 0;
		estimatedBytes = 0;

		foreach (var subview in view.Subviews)
		{
			if (subview is UILabel label)
			{
				var bytes = EstimateTextBytes(label.Text, label.AttributedText?.Value);
				if (bytes >= PayloadBytesPerChildLabel * 0.95)
				{
					count++;
					estimatedBytes += Math.Min(bytes, PayloadBytesPerChildLabel);
				}
			}

			count += CountPayloadLabels(subview, out var nestedBytes);
			estimatedBytes += nestedBytes;
		}

		return count;
	}

	static int CountSubviews(UIView view)
	{
		var count = view.Subviews.Length;

		foreach (var subview in view.Subviews)
			count += CountSubviews(subview);

		return count;
	}

	static long EstimateTextBytes(string? text, string? attributedText)
	{
		var retainedText = attributedText ?? text;
		return string.IsNullOrEmpty(retainedText) ? 0 : retainedText.Length * 2L;
	}

	static RetainedParentPeer RetainNativeParent(string handlerKind, PlatformContentView peer)
	{
		var handle = peer.Handle;
		if (handle == IntPtr.Zero)
			throw new InvalidOperationException($"Cannot retain a native {handlerKind} parent peer with a zero handle.");

		var retained = IntPtr_objc_msgSend(handle, RetainSelector);
		if (retained == IntPtr.Zero)
			throw new InvalidOperationException("Objective-C retain returned a zero handle.");

		return new RetainedParentPeer(handlerKind, retained);
	}

	static ParentSnapshot GetParentSnapshot(RetainedParentPeer retainedParent)
	{
		var parent = retainedParent.TryGetPeer();
		if (parent is null)
			return new ParentSnapshot(Alive: false, Subviews: 0, PayloadLabels: 0, EstimatedPayloadBytes: 0);

		var payloadLabels = CountPayloadLabels(parent, out var estimatedPayloadBytes);
		return new ParentSnapshot(
			Alive: true,
			Subviews: CountSubviews(parent),
			PayloadLabels: payloadLabels,
			EstimatedPayloadBytes: estimatedPayloadBytes);
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

	internal sealed record ParentSnapshot(bool Alive, int Subviews, int PayloadLabels, long EstimatedPayloadBytes);

	internal sealed class RetainedParentPeer
	{
		public RetainedParentPeer(string handlerKind, IntPtr handle)
		{
			HandlerKind = handlerKind;
			Handle = handle;
		}

		public string HandlerKind { get; }

		public IntPtr Handle { get; }

		public PlatformContentView? TryGetPeer()
		{
			if (Handle == IntPtr.Zero)
				return null;

			try
			{
				return Runtime.GetNSObject<PlatformContentView>(Handle, false);
			}
			catch
			{
				return null;
			}
		}
	}

	internal sealed record TrackedCycle(
		string HandlerKind,
		WeakReference<object> ParentView,
		WeakReference<object> ParentHandler,
		WeakReference<object> ChildView,
		WeakReference<object> ChildHandler,
		WeakReference<object> ChildNativeLabel)
	{
		public static TrackedCycle Create(
			string handlerKind,
			object parentView,
			object parentHandler,
			object childView,
			object childHandler,
			object childNativeLabel)
		{
			return new TrackedCycle(
				handlerKind,
				new WeakReference<object>(parentView),
				new WeakReference<object>(parentHandler),
				new WeakReference<object>(childView),
				new WeakReference<object>(childHandler),
				new WeakReference<object>(childNativeLabel));
		}
	}

	internal sealed record GroupResult(
		string HandlerKind,
		int TrackedCycles,
		int RetainedNativeParents,
		int ParentsWithSubviews,
		int PayloadNativeLabels,
		long EstimatedPayloadBytes,
		int AliveParentViews,
		int AliveParentHandlers,
		int AliveChildViews,
		int AliveChildHandlers,
		int AliveChildNativeLabelWrappers);

	internal sealed record ScenarioResult(
		string Name,
		int TrackedCycles,
		int RetainedNativeParents,
		int ParentsWithSubviews,
		int PayloadNativeLabels,
		long EstimatedPayloadBytes,
		int AliveParentViews,
		int AliveParentHandlers,
		int AliveChildViews,
		int AliveChildHandlers,
		int AliveChildNativeLabelWrappers,
		IReadOnlyList<GroupResult> Groups)
	{
		internal static ScenarioResult From(
			string name,
			IReadOnlyList<string> handlerKinds,
			IReadOnlyList<RetainedParentPeer> retainedParents,
			IReadOnlyList<TrackedCycle> tracked)
		{
			var groups = new List<GroupResult>(handlerKinds.Count);
			var totals = GroupResultFrom("all", retainedParents, tracked);

			foreach (var handlerKind in handlerKinds)
			{
				var retainedForKind = retainedParents.Where(parent => parent.HandlerKind == handlerKind).ToArray();
				var trackedForKind = tracked.Where(cycle => cycle.HandlerKind == handlerKind).ToArray();
				groups.Add(GroupResultFrom(handlerKind, retainedForKind, trackedForKind));
			}

			return new ScenarioResult(
				name,
				totals.TrackedCycles,
				totals.RetainedNativeParents,
				totals.ParentsWithSubviews,
				totals.PayloadNativeLabels,
				totals.EstimatedPayloadBytes,
				totals.AliveParentViews,
				totals.AliveParentHandlers,
				totals.AliveChildViews,
				totals.AliveChildHandlers,
				totals.AliveChildNativeLabelWrappers,
				groups);
		}

		static GroupResult GroupResultFrom(
			string handlerKind,
			IReadOnlyList<RetainedParentPeer> retainedParents,
			IReadOnlyList<TrackedCycle> tracked)
		{
			var retainedNativeParents = 0;
			var parentsWithSubviews = 0;
			var payloadNativeLabels = 0;
			long estimatedPayloadBytes = 0;

			foreach (var retainedParent in retainedParents)
			{
				var snapshot = GetParentSnapshot(retainedParent);
				if (!snapshot.Alive)
					continue;

				retainedNativeParents++;

				if (snapshot.Subviews > 0)
					parentsWithSubviews++;

				payloadNativeLabels += snapshot.PayloadLabels;
				estimatedPayloadBytes += snapshot.EstimatedPayloadBytes;
			}

			var aliveParentViews = 0;
			var aliveParentHandlers = 0;
			var aliveChildViews = 0;
			var aliveChildHandlers = 0;
			var aliveChildNativeLabelWrappers = 0;

			foreach (var cycle in tracked)
			{
				if (cycle.ParentView.TryGetTarget(out _))
					aliveParentViews++;

				if (cycle.ParentHandler.TryGetTarget(out _))
					aliveParentHandlers++;

				if (cycle.ChildView.TryGetTarget(out _))
					aliveChildViews++;

				if (cycle.ChildHandler.TryGetTarget(out _))
					aliveChildHandlers++;

				if (cycle.ChildNativeLabel.TryGetTarget(out _))
					aliveChildNativeLabelWrappers++;
			}

			return new GroupResult(
				handlerKind,
				tracked.Count,
				retainedNativeParents,
				parentsWithSubviews,
				payloadNativeLabels,
				estimatedPayloadBytes,
				aliveParentViews,
				aliveParentHandlers,
				aliveChildViews,
				aliveChildHandlers,
				aliveChildNativeLabelWrappers);
		}
	}
}

internal sealed record ReproReport(
	int CyclesPerHandlerType,
	int HandlerTypeCount,
	int PayloadKiBPerChildLabel,
	long BaselineManagedBytes,
	long FinalManagedBytes,
	ReproSession.ScenarioResult Control,
	ReproSession.ScenarioResult Current)
{
	int TotalCycles => CyclesPerHandlerType * HandlerTypeCount;

	public bool LeakProved =>
		Control.RetainedNativeParents == TotalCycles &&
		Control.ParentsWithSubviews == 0 &&
		Control.PayloadNativeLabels == 0 &&
		Control.EstimatedPayloadBytes == 0 &&
		Current.RetainedNativeParents == TotalCycles &&
		Current.ParentsWithSubviews == TotalCycles &&
		Current.PayloadNativeLabels == TotalCycles &&
		Current.EstimatedPayloadBytes >= TotalCycles * PayloadKiBPerChildLabel * 1024L * 0.95 &&
		Current.AliveParentViews <= HandlerTypeCount &&
		Current.AliveParentHandlers <= HandlerTypeCount &&
		Current.AliveChildViews <= HandlerTypeCount &&
		Current.AliveChildHandlers <= HandlerTypeCount &&
		Control.Groups.All(group => ControlGroupIsClean(group)) &&
		Current.Groups.All(group => CurrentGroupProvesLeak(group));

	public string ToText()
	{
		var heapDeltaMiB = (FinalManagedBytes - BaselineManagedBytes) / 1024d / 1024d;
		var controlMiB = Control.EstimatedPayloadBytes / 1024d / 1024d;
		var currentMiB = Current.EstimatedPayloadBytes / 1024d / 1024d;

		return string.Join(Environment.NewLine,
			"IosContentBackedHandlerNativeSubviewRetentionRepro",
			$"Handler types: {HandlerTypeCount}",
			$"Cycles per handler type per scenario: {CyclesPerHandlerType}",
			$"Total cycles per scenario: {TotalCycles}",
			$"Payload per child native UILabel: {PayloadKiBPerChildLabel} KiB",
			$"Baseline managed heap: {BaselineManagedBytes:N0} bytes",
			$"Final managed heap: {FinalManagedBytes:N0} bytes",
			$"Managed heap delta: {heapDeltaMiB:N1} MiB",
			$"Leak proved: {LeakProved}",
			string.Empty,
			Format(Control),
			string.Empty,
			Format(Current),
			string.Empty,
			$"Control estimated retained native label payload: {controlMiB:N1} MiB",
			$"Current estimated retained native label payload: {currentMiB:N1} MiB",
			$"RESULT: {(LeakProved ? "PROVEN" : "NOT PROVEN")}");
	}

	bool ControlGroupIsClean(ReproSession.GroupResult group) =>
		group.TrackedCycles == CyclesPerHandlerType &&
		group.RetainedNativeParents == CyclesPerHandlerType &&
		group.ParentsWithSubviews == 0 &&
		group.PayloadNativeLabels == 0 &&
		group.EstimatedPayloadBytes == 0;

	bool CurrentGroupProvesLeak(ReproSession.GroupResult group) =>
		group.TrackedCycles == CyclesPerHandlerType &&
		group.RetainedNativeParents == CyclesPerHandlerType &&
		group.ParentsWithSubviews == CyclesPerHandlerType &&
		group.PayloadNativeLabels == CyclesPerHandlerType &&
		group.EstimatedPayloadBytes >= CyclesPerHandlerType * PayloadKiBPerChildLabel * 1024L * 0.95;

	static string Format(ReproSession.ScenarioResult result)
	{
		var retainedMiB = result.EstimatedPayloadBytes / 1024d / 1024d;
		return string.Join(Environment.NewLine,
			$"Run: {result.Name}",
			$"  tracked cycles: {result.TrackedCycles}",
			$"  retained native ContentView parents: {result.RetainedNativeParents}/{result.TrackedCycles}",
			$"  retained parents with subviews: {result.ParentsWithSubviews}/{result.TrackedCycles}",
			$"  native UILabel payload subviews: {result.PayloadNativeLabels}/{result.TrackedCycles}",
			$"  estimated retained native label bytes: {result.EstimatedPayloadBytes:N0}",
			$"  estimated retained native label MiB: {retainedMiB:N1}",
			$"  alive parent views: {result.AliveParentViews}/{result.TrackedCycles}",
			$"  alive parent handlers: {result.AliveParentHandlers}/{result.TrackedCycles}",
			$"  alive child Labels: {result.AliveChildViews}/{result.TrackedCycles}",
			$"  alive child handlers: {result.AliveChildHandlers}/{result.TrackedCycles}",
			$"  alive child native UILabel wrappers: {result.AliveChildNativeLabelWrappers}/{result.TrackedCycles}",
			FormatGroups(result.Groups));
	}

	static string FormatGroups(IReadOnlyList<ReproSession.GroupResult> groups)
	{
		var lines = new List<string>();

		foreach (var group in groups)
		{
			var retainedMiB = group.EstimatedPayloadBytes / 1024d / 1024d;
			lines.Add($"  group {group.HandlerKind}:");
			lines.Add($"    tracked cycles: {group.TrackedCycles}");
			lines.Add($"    retained native ContentView parents: {group.RetainedNativeParents}/{group.TrackedCycles}");
			lines.Add($"    retained parents with subviews: {group.ParentsWithSubviews}/{group.TrackedCycles}");
			lines.Add($"    native UILabel payload subviews: {group.PayloadNativeLabels}/{group.TrackedCycles}");
			lines.Add($"    estimated retained native label MiB: {retainedMiB:N1}");
			lines.Add($"    alive parent views: {group.AliveParentViews}/{group.TrackedCycles}");
			lines.Add($"    alive parent handlers: {group.AliveParentHandlers}/{group.TrackedCycles}");
			lines.Add($"    alive child Labels: {group.AliveChildViews}/{group.TrackedCycles}");
			lines.Add($"    alive child handlers: {group.AliveChildHandlers}/{group.TrackedCycles}");
			lines.Add($"    alive child native UILabel wrappers: {group.AliveChildNativeLabelWrappers}/{group.TrackedCycles}");
		}

		return string.Join(Environment.NewLine, lines);
	}
}
