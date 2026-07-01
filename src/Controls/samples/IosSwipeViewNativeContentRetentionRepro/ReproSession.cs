#nullable enable

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Reflection;
using System.Text;
using System.Threading;
using CoreGraphics;
using Foundation;
using Microsoft.Maui;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Handlers;
using Microsoft.Maui.Platform;
using ObjCRuntime;
using UIKit;
using PlatformSwipeView = Microsoft.Maui.Platform.MauiSwipeView;

namespace IosSwipeViewNativeContentRetentionRepro;

internal static class ReproSession
{
	internal const int Cycles = 128;
	internal const int PayloadKiBPerChildLabel = 256;
	const long PayloadBytesPerChildLabel = PayloadKiBPerChildLabel * 1024L;

	static readonly IntPtr RetainSelector = Selector.GetHandle("retain");
	static readonly FieldInfo ContentViewField =
		typeof(PlatformSwipeView).GetField("_contentView", BindingFlags.Instance | BindingFlags.NonPublic)
		?? throw new MissingFieldException(typeof(PlatformSwipeView).FullName, "_contentView");
	static readonly List<IReadOnlyList<RetainedParentPeer>> RetainedNativeParents = new();

	public static readonly string ResultsPath =
		Path.Combine("/tmp", "ios-swipeview-native-content-retention-results.txt");

	public static ReproReport Run(IMauiContext mauiContext)
	{
		WriteProgress("Starting iOS/Mac Catalyst SwipeView native content retention repro.");
		ForceFullGc();
		var baselineBytes = GC.GetTotalMemory(true);

		WriteProgress("Running control scenario.");
		var control = RunScenario(
			"control: retained native MauiSwipeView parents after explicit native content cleanup",
			mauiContext,
			clearNativeContentAfterDisconnect: true);

		WriteProgress("Running current MAUI scenario.");
		var current = RunScenario(
			"current: SwipeViewHandler.DisconnectHandler leaves current native content attached",
			mauiContext,
			clearNativeContentAfterDisconnect: false);

		WriteProgress("Finalizing report.");
		ForceFullGc();
		var finalBytes = GC.GetTotalMemory(true);

		GC.KeepAlive(RetainedNativeParents);

		return new ReproReport(
			Cycles,
			PayloadKiBPerChildLabel,
			baselineBytes,
			finalBytes,
			control,
			current);
	}

	static ScenarioResult RunScenario(
		string name,
		IMauiContext mauiContext,
		bool clearNativeContentAfterDisconnect)
	{
		var retainedParents = new List<RetainedParentPeer>(Cycles);
		var tracked = new List<TrackedCycle>(Cycles);

		for (var i = 0; i < Cycles; i++)
		{
			if (i % 32 == 0)
				WriteProgress($"{name}: cycle {i}/{Cycles}");

			CreateSwipeViewCycle(i, mauiContext, retainedParents, tracked, clearNativeContentAfterDisconnect);
		}

		RetainedNativeParents.Add(retainedParents);
		ForceFullGc();

		return ScenarioResult.From(name, retainedParents, tracked);
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	static void CreateSwipeViewCycle(
		int cycle,
		IMauiContext mauiContext,
		List<RetainedParentPeer> retainedParents,
		List<TrackedCycle> tracked,
		bool clearNativeContentAfterDisconnect)
	{
		using var pool = new NSAutoreleasePool();

		var child = new Label
		{
			AutomationId = $"swipeview-child-label-{cycle:000}",
			Text = CreatePayload(cycle),
			LineBreakMode = LineBreakMode.NoWrap,
			WidthRequest = 900,
			HeightRequest = 44
		};

		var parent = new SwipeView
		{
			AutomationId = $"swipeview-parent-{cycle:000}",
			Content = child,
			WidthRequest = 900,
			HeightRequest = 56
		};

		parent.Layout(new Rect(0, 0, 900, 56));
		child.Layout(new Rect(0, 0, 900, 44));

		var handler = (SwipeViewHandler)parent.ToHandler(mauiContext);
		var platformParent = (PlatformSwipeView)handler.PlatformView;
		SetRealisticBounds(platformParent, 900, 56);
		handler.UpdateValue(nameof(IContentView.Content));

		var nativeLabel = FindPayloadLabel(platformParent)
			?? throw new InvalidOperationException("SwipeViewHandler did not attach a native UILabel child with the expected payload.");

		var childHandler = child.Handler
			?? throw new InvalidOperationException("SwipeView content Label did not receive a handler.");

		var retainedParent = RetainNativeParent(platformParent);

		((IElementHandler)handler).DisconnectHandler();

		if (clearNativeContentAfterDisconnect)
			ClearNativeContent(platformParent);

		parent.BindingContext = null;
		child.BindingContext = null;

		retainedParents.Add(retainedParent);
		tracked.Add(TrackedCycle.Create(parent, handler, child, childHandler, nativeLabel));
	}

	static string CreatePayload(int cycle)
	{
		var targetChars = (int)(PayloadBytesPerChildLabel / 2);
		var sentence =
			$"generated swipe audit row {cycle:0000} with transaction notes, review comments, localized status, and audit metadata. ";
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

	static UIView? GetContentViewField(PlatformSwipeView parent)
	{
		return ContentViewField.GetValue(parent) as UIView;
	}

	static void ClearNativeContent(PlatformSwipeView parent)
	{
		parent.ClearSubviews();
		ContentViewField.SetValue(parent, null);
	}

	static bool ContainsSubviewReference(UIView root, UIView candidate)
	{
		foreach (var subview in root.Subviews)
		{
			if (ReferenceEquals(subview, candidate))
				return true;

			if (ContainsSubviewReference(subview, candidate))
				return true;
		}

		return false;
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

	static RetainedParentPeer RetainNativeParent(PlatformSwipeView peer)
	{
		var handle = peer.Handle;
		if (handle == IntPtr.Zero)
			throw new InvalidOperationException("Cannot retain a native MauiSwipeView peer with a zero handle.");

		var retained = IntPtr_objc_msgSend(handle, RetainSelector);
		if (retained == IntPtr.Zero)
			throw new InvalidOperationException("Objective-C retain returned a zero handle.");

		return new RetainedParentPeer(retained);
	}

	static ParentSnapshot GetParentSnapshot(RetainedParentPeer retainedParent)
	{
		var parent = retainedParent.TryGetPeer();
		if (parent is null)
			return new ParentSnapshot(Alive: false, Subviews: 0, ContentFieldAssigned: false, PayloadLabels: 0, EstimatedPayloadBytes: 0);

		var payloadLabels = CountPayloadLabels(parent, out var estimatedPayloadBytes);
		var contentView = GetContentViewField(parent);

		if (contentView is not null && !ContainsSubviewReference(parent, contentView))
		{
			payloadLabels += CountPayloadLabels(contentView, out var contentViewPayloadBytes);
			estimatedPayloadBytes += contentViewPayloadBytes;
		}

		return new ParentSnapshot(
			Alive: true,
			Subviews: CountSubviews(parent),
			ContentFieldAssigned: contentView is not null,
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

	internal sealed record ParentSnapshot(bool Alive, int Subviews, bool ContentFieldAssigned, int PayloadLabels, long EstimatedPayloadBytes);

	internal sealed class RetainedParentPeer
	{
		public RetainedParentPeer(IntPtr handle)
		{
			Handle = handle;
		}

		public IntPtr Handle { get; }

		public PlatformSwipeView? TryGetPeer()
		{
			if (Handle == IntPtr.Zero)
				return null;

			try
			{
				return Runtime.GetNSObject<PlatformSwipeView>(Handle, false);
			}
			catch
			{
				return null;
			}
		}
	}

	internal sealed record TrackedCycle(
		WeakReference<object> ParentView,
		WeakReference<object> ParentHandler,
		WeakReference<object> ChildView,
		WeakReference<object> ChildHandler,
		WeakReference<object> ChildNativeLabel)
	{
		public static TrackedCycle Create(
			object parentView,
			object parentHandler,
			object childView,
			object childHandler,
			object childNativeLabel)
		{
			return new TrackedCycle(
				new WeakReference<object>(parentView),
				new WeakReference<object>(parentHandler),
				new WeakReference<object>(childView),
				new WeakReference<object>(childHandler),
				new WeakReference<object>(childNativeLabel));
		}
	}

	internal sealed record ScenarioResult(
		string Name,
		int TrackedCycles,
		int RetainedNativeParents,
		int ParentsWithSubviews,
		int ParentsWithContentField,
		int PayloadNativeLabels,
		long EstimatedPayloadBytes,
		int AliveParentViews,
		int AliveParentHandlers,
		int AliveChildViews,
		int AliveChildHandlers,
		int AliveChildNativeLabelWrappers)
	{
		internal static ScenarioResult From(
			string name,
			IReadOnlyList<RetainedParentPeer> retainedParents,
			IReadOnlyList<TrackedCycle> tracked)
		{
			var retainedNativeParents = 0;
			var parentsWithSubviews = 0;
			var parentsWithContentField = 0;
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

				if (snapshot.ContentFieldAssigned)
					parentsWithContentField++;

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

			return new ScenarioResult(
				name,
				tracked.Count,
				retainedNativeParents,
				parentsWithSubviews,
				parentsWithContentField,
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
	int Cycles,
	int PayloadKiBPerChildLabel,
	long BaselineManagedBytes,
	long FinalManagedBytes,
	ReproSession.ScenarioResult Control,
	ReproSession.ScenarioResult Current)
{
	public bool LeakProved =>
		Control.RetainedNativeParents == Cycles &&
		Control.ParentsWithSubviews == 0 &&
		Control.ParentsWithContentField == 0 &&
		Control.PayloadNativeLabels == 0 &&
		Control.EstimatedPayloadBytes == 0 &&
		Current.RetainedNativeParents == Cycles &&
		Current.ParentsWithSubviews == Cycles &&
		Current.ParentsWithContentField == Cycles &&
		Current.PayloadNativeLabels == Cycles &&
		Current.EstimatedPayloadBytes >= Cycles * PayloadKiBPerChildLabel * 1024L * 0.95 &&
		Current.AliveParentViews <= 1 &&
		Current.AliveParentHandlers <= 1 &&
		Current.AliveChildViews <= 1 &&
		Current.AliveChildHandlers <= 1;

	public string ToText()
	{
		var heapDeltaMiB = (FinalManagedBytes - BaselineManagedBytes) / 1024d / 1024d;
		var controlMiB = Control.EstimatedPayloadBytes / 1024d / 1024d;
		var currentMiB = Current.EstimatedPayloadBytes / 1024d / 1024d;

		return string.Join(Environment.NewLine,
			"IosSwipeViewNativeContentRetentionRepro",
			$"Cycles per scenario: {Cycles}",
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

	static string Format(ReproSession.ScenarioResult result)
	{
		var retainedMiB = result.EstimatedPayloadBytes / 1024d / 1024d;
		return string.Join(Environment.NewLine,
			$"Run: {result.Name}",
			$"  tracked cycles: {result.TrackedCycles}",
			$"  retained native MauiSwipeView parents: {result.RetainedNativeParents}/{result.TrackedCycles}",
			$"  retained parents with subviews: {result.ParentsWithSubviews}/{result.TrackedCycles}",
			$"  retained parents with _contentView assigned: {result.ParentsWithContentField}/{result.TrackedCycles}",
			$"  native UILabel payload subviews: {result.PayloadNativeLabels}/{result.TrackedCycles}",
			$"  estimated retained native label bytes: {result.EstimatedPayloadBytes:N0}",
			$"  estimated retained native label MiB: {retainedMiB:N1}",
			$"  alive parent SwipeViews: {result.AliveParentViews}/{result.TrackedCycles}",
			$"  alive parent handlers: {result.AliveParentHandlers}/{result.TrackedCycles}",
			$"  alive child Labels: {result.AliveChildViews}/{result.TrackedCycles}",
			$"  alive child handlers: {result.AliveChildHandlers}/{result.TrackedCycles}",
			$"  alive child native UILabel wrappers: {result.AliveChildNativeLabelWrappers}/{result.TrackedCycles}");
	}
}
