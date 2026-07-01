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

namespace IosGeneralWrapperViewNativeChildRetentionRepro;

internal static class ReproSession
{
	internal const int Cycles = 128;
	internal const int PayloadKiBPerChildLabel = 256;
	const long PayloadBytesPerChildLabel = PayloadKiBPerChildLabel * 1024L;

	static readonly IntPtr RetainSelector = Selector.GetHandle("retain");
	static readonly Type GeneralWrapperViewType =
		typeof(MauiView).Assembly.GetType("Microsoft.Maui.Platform.GeneralWrapperView", throwOnError: true)
		?? throw new TypeLoadException("Microsoft.Maui.Platform.GeneralWrapperView");
	static readonly ConstructorInfo GeneralWrapperViewConstructor =
		GeneralWrapperViewType.GetConstructor(
			BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
			binder: null,
			new[] { typeof(IView), typeof(IMauiContext) },
			modifiers: null)
		?? throw new MissingMethodException(GeneralWrapperViewType.FullName, ".ctor(IView, IMauiContext)");
	static readonly MethodInfo GeneralWrapperViewDisconnect =
		GeneralWrapperViewType.GetMethod("Disconnect", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
		?? throw new MissingMethodException(GeneralWrapperViewType.FullName, "Disconnect");
	static readonly List<IReadOnlyList<RetainedParentPeer>> RetainedNativeParents = new();

	public static readonly string ResultsPath =
		Path.Combine("/tmp", "ios-generalwrapperview-native-child-retention-results.txt");

	public static ReproReport Run(IMauiContext mauiContext)
	{
		WriteProgress("Starting iOS/Mac Catalyst GeneralWrapperView native child retention repro.");
		ForceFullGc();
		var baselineBytes = GC.GetTotalMemory(true);

		WriteProgress("Running control scenario.");
		var control = RunScenario(
			"control: retained native GeneralWrapperView parents after explicit ClearSubviews cleanup",
			mauiContext,
			clearSubviewsAfterDisconnect: true);

		WriteProgress("Running current MAUI scenario.");
		var current = RunScenario(
			"current: GeneralWrapperView.Disconnect leaves native child subview attached",
			mauiContext,
			clearSubviewsAfterDisconnect: false);

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
		bool clearSubviewsAfterDisconnect)
	{
		var retainedParents = new List<RetainedParentPeer>(Cycles);
		var tracked = new List<TrackedCycle>(Cycles);

		for (var i = 0; i < Cycles; i++)
		{
			if (i % 32 == 0)
				WriteProgress($"{name}: cycle {i}/{Cycles}");

			CreateGeneralWrapperViewCycle(i, mauiContext, retainedParents, tracked, clearSubviewsAfterDisconnect);
		}

		RetainedNativeParents.Add(retainedParents);
		ForceFullGc();

		return ScenarioResult.From(name, retainedParents, tracked);
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	static void CreateGeneralWrapperViewCycle(
		int cycle,
		IMauiContext mauiContext,
		List<RetainedParentPeer> retainedParents,
		List<TrackedCycle> tracked,
		bool clearSubviewsAfterDisconnect)
	{
		using var pool = new NSAutoreleasePool();

		var child = new Label
		{
			AutomationId = $"generalwrapperview-child-label-{cycle:000}",
			Text = CreatePayload(cycle),
			LineBreakMode = LineBreakMode.NoWrap,
			WidthRequest = 900,
			HeightRequest = 44
		};

		child.Layout(new Rect(0, 0, 900, 44));

		var platformParent = CreateGeneralWrapperView(child, mauiContext);
		SetRealisticBounds(platformParent, 900, 56);

		var nativeLabel = FindPayloadLabel(platformParent)
			?? throw new InvalidOperationException("GeneralWrapperView did not attach a native UILabel child with the expected payload.");

		var childHandler = child.Handler
			?? throw new InvalidOperationException("GeneralWrapperView child Label did not receive a handler.");

		var retainedParent = RetainNativeParent(platformParent);

		DisconnectGeneralWrapperView(platformParent);

		if (clearSubviewsAfterDisconnect)
			platformParent.ClearSubviews();

		child.BindingContext = null;

		retainedParents.Add(retainedParent);
		tracked.Add(TrackedCycle.Create(platformParent, child, childHandler, nativeLabel));
	}

	static string CreatePayload(int cycle)
	{
		var targetChars = (int)(PayloadBytesPerChildLabel / 2);
		var sentence =
			$"generated general wrapper audit row {cycle:0000} with transaction notes, review comments, localized status, and audit metadata. ";
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

	static UIView CreateGeneralWrapperView(IView child, IMauiContext mauiContext)
	{
		return (UIView)GeneralWrapperViewConstructor.Invoke(new object[] { child, mauiContext });
	}

	static void DisconnectGeneralWrapperView(UIView parent)
	{
		GeneralWrapperViewDisconnect.Invoke(parent, parameters: null);
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

	static RetainedParentPeer RetainNativeParent(UIView peer)
	{
		var handle = peer.Handle;
		if (handle == IntPtr.Zero)
			throw new InvalidOperationException("Cannot retain a native GeneralWrapperView peer with a zero handle.");

		var retained = IntPtr_objc_msgSend(handle, RetainSelector);
		if (retained == IntPtr.Zero)
			throw new InvalidOperationException("Objective-C retain returned a zero handle.");

		return new RetainedParentPeer(retained);
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
		public RetainedParentPeer(IntPtr handle)
		{
			Handle = handle;
		}

		public IntPtr Handle { get; }

		public UIView? TryGetPeer()
		{
			if (Handle == IntPtr.Zero)
				return null;

			try
			{
				return Runtime.GetNSObject<UIView>(Handle, false);
			}
			catch
			{
				return null;
			}
		}
	}

	internal sealed record TrackedCycle(
		WeakReference<object> WrapperView,
		WeakReference<object> ChildView,
		WeakReference<object> ChildHandler,
		WeakReference<object> ChildNativeLabel)
	{
		public static TrackedCycle Create(
			object wrapperView,
			object childView,
			object childHandler,
			object childNativeLabel)
		{
			return new TrackedCycle(
				new WeakReference<object>(wrapperView),
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
		int PayloadNativeLabels,
		long EstimatedPayloadBytes,
		int AliveManagedWrappers,
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

			var aliveManagedWrappers = 0;
			var aliveChildViews = 0;
			var aliveChildHandlers = 0;
			var aliveChildNativeLabelWrappers = 0;

			foreach (var cycle in tracked)
			{
				if (cycle.WrapperView.TryGetTarget(out _))
					aliveManagedWrappers++;

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
				payloadNativeLabels,
				estimatedPayloadBytes,
				aliveManagedWrappers,
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
		Control.PayloadNativeLabels == 0 &&
		Control.EstimatedPayloadBytes == 0 &&
		Current.RetainedNativeParents == Cycles &&
		Current.ParentsWithSubviews == Cycles &&
		Current.PayloadNativeLabels == Cycles &&
		Current.EstimatedPayloadBytes >= Cycles * PayloadKiBPerChildLabel * 1024L * 0.95 &&
		Current.AliveChildViews <= 1 &&
		Current.AliveChildHandlers <= 1;

	public string ToText()
	{
		var heapDeltaMiB = (FinalManagedBytes - BaselineManagedBytes) / 1024d / 1024d;
		var controlMiB = Control.EstimatedPayloadBytes / 1024d / 1024d;
		var currentMiB = Current.EstimatedPayloadBytes / 1024d / 1024d;

		return string.Join(Environment.NewLine,
			"IosGeneralWrapperViewNativeChildRetentionRepro",
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
			$"  retained native GeneralWrapperView parents: {result.RetainedNativeParents}/{result.TrackedCycles}",
			$"  retained parents with subviews: {result.ParentsWithSubviews}/{result.TrackedCycles}",
			$"  native UILabel payload subviews: {result.PayloadNativeLabels}/{result.TrackedCycles}",
			$"  estimated retained native label bytes: {result.EstimatedPayloadBytes:N0}",
			$"  estimated retained native label MiB: {retainedMiB:N1}",
			$"  alive managed GeneralWrapperView wrappers: {result.AliveManagedWrappers}/{result.TrackedCycles}",
			$"  alive child Labels: {result.AliveChildViews}/{result.TrackedCycles}",
			$"  alive child handlers: {result.AliveChildHandlers}/{result.TrackedCycles}",
			$"  alive child native UILabel wrappers: {result.AliveChildNativeLabelWrappers}/{result.TrackedCycles}");
	}
}
