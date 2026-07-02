using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using Android.Views;
using AndroidX.CoordinatorLayout.Widget;
using AndroidX.Core.View;
using Microsoft.Maui;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Handlers;
using AView = Android.Views.View;

namespace AndroidFlyoutViewInsetListenerRetentionRepro;

static class ReproSession
{
	const int CycleCount = 80;
	const int PayloadBytes = 1024 * 1024;

	static readonly List<AView> RetainedNavigationRoots = new();

	static readonly FieldInfo NavigationRootField =
		typeof(FlyoutViewHandler).GetField("_navigationRoot", BindingFlags.Instance | BindingFlags.NonPublic)
		?? throw new MissingFieldException(nameof(FlyoutViewHandler), "_navigationRoot");

	static readonly Type MauiWindowInsetListenerType =
		typeof(FlyoutViewHandler).Assembly.GetType("Microsoft.Maui.Platform.MauiWindowInsetListener")
		?? throw new MissingMemberException("Microsoft.Maui.Platform.MauiWindowInsetListener");

	static readonly MethodInfo FindListenerForViewMethod =
		MauiWindowInsetListenerType.GetMethod("FindListenerForView", BindingFlags.Static | BindingFlags.NonPublic)
		?? throw new MissingMethodException(MauiWindowInsetListenerType.FullName, "FindListenerForView");

	static readonly MethodInfo TrackViewMethod =
		MauiWindowInsetListenerType.GetMethod("TrackView", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
		?? throw new MissingMethodException(MauiWindowInsetListenerType.FullName, "TrackView");

	public static ReproReport Run(IMauiContext mauiContext)
	{
		RetainedNavigationRoots.Clear();
		ForceFullCollection();

		var control = RunScenario(
			mauiContext,
			"control: clear CoordinatorLayout inset listener slots before FlyoutViewHandler disconnect",
			clearInsetListenerBeforeDisconnect: true);

		var current = RunScenario(
			mauiContext,
			"current disconnect: FlyoutViewHandler unregisters registry entries but leaves native inset listener slots",
			clearInsetListenerBeforeDisconnect: false);

		GC.KeepAlive(RetainedNavigationRoots);
		return new ReproReport(control, current);
	}

	static ScenarioResult RunScenario(
		IMauiContext mauiContext,
		string name,
		bool clearInsetListenerBeforeDisconnect)
	{
		var rootReferences = new List<WeakReference<AView>>(CycleCount);
		var listenerReferences = new List<WeakReference>(CycleCount);
		var childPlatformReferences = new List<WeakReference<PayloadNativeView>>(CycleCount);
		var payloadReferences = new List<WeakReference<LeakPayload>>(CycleCount);

		for (var i = 0; i < CycleCount; i++)
		{
			CreateDisconnectedCycle(
				mauiContext,
				i,
				clearInsetListenerBeforeDisconnect,
				rootReferences,
				listenerReferences,
				childPlatformReferences,
				payloadReferences);
		}

		ForceFullCollection();

		var result = new ScenarioResult(
			name,
			AliveNavigationRoots: CountAlive(rootReferences),
			AliveInsetListeners: CountAlive(listenerReferences),
			AliveRemovedChildPlatformViews: CountAlive(childPlatformReferences),
			AlivePayloads: CountAlive(payloadReferences));

		GC.KeepAlive(RetainedNavigationRoots);
		return result;
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	static void CreateDisconnectedCycle(
		IMauiContext mauiContext,
		int index,
		bool clearInsetListenerBeforeDisconnect,
		List<WeakReference<AView>> rootReferences,
		List<WeakReference> listenerReferences,
		List<WeakReference<PayloadNativeView>> childPlatformReferences,
		List<WeakReference<LeakPayload>> payloadReferences)
	{
		var handler = new FlyoutViewHandler();
		var flyoutPage = CreateFlyoutPage(index);

		handler.SetMauiContext(mauiContext);
		handler.SetVirtualView(flyoutPage);

		if (NavigationRootField.GetValue(handler) is not CoordinatorLayout navigationRoot)
			throw new InvalidOperationException("FlyoutViewHandler did not create a CoordinatorLayout navigation root.");

		var temporaryChild = new AView(mauiContext.Context);
		navigationRoot.AddView(temporaryChild);
		var listener = FindInsetListener(temporaryChild)
			?? throw new InvalidOperationException("No MauiWindowInsetListener was registered for the tracked child view.");
		navigationRoot.RemoveView(temporaryChild);
		temporaryChild.Dispose();

		var context = mauiContext.Context ?? throw new InvalidOperationException("MauiContext.Context is null.");
		var payload = new LeakPayload(index, PayloadBytes);
		var childPlatformView = new PayloadNativeView(context, index, payload);

		TrackViewMethod.Invoke(listener, new object[] { childPlatformView });

		RetainedNavigationRoots.Add(navigationRoot);
		rootReferences.Add(new WeakReference<AView>(navigationRoot));
		listenerReferences.Add(new WeakReference(listener));
		childPlatformReferences.Add(new WeakReference<PayloadNativeView>(childPlatformView));
		payloadReferences.Add(new WeakReference<LeakPayload>(payload));

		if (clearInsetListenerBeforeDisconnect)
			ClearNativeInsetListenerSlots(navigationRoot);

		((IElementHandler)handler).DisconnectHandler();
	}

	static object? FindInsetListener(AView view)
	{
		return FindListenerForViewMethod.Invoke(null, new object[] { view });
	}

	static void ClearNativeInsetListenerSlots(AView view)
	{
		ViewCompat.SetOnApplyWindowInsetsListener(view, null);
		ViewCompat.SetWindowInsetsAnimationCallback(view, null);
	}

	static FlyoutPage CreateFlyoutPage(int index)
	{
		return new FlyoutPage
		{
			Flyout = new ContentPage
			{
				Title = $"Flyout {index}",
				Content = new Label { Text = $"Flyout {index}" }
			},
			Detail = new ContentPage
			{
				Title = $"Detail {index}",
				Content = new Label { Text = $"Detail {index}" }
			},
			FlyoutLayoutBehavior = FlyoutLayoutBehavior.Popover
		};
	}

	static int CountAlive<T>(List<WeakReference<T>> references)
		where T : class
	{
		var count = 0;
		foreach (var reference in references)
		{
			if (reference.TryGetTarget(out _))
				count++;
		}

		return count;
	}

	static int CountAlive(List<WeakReference> references)
	{
		var count = 0;
		foreach (var reference in references)
		{
			if (reference.IsAlive)
				count++;
		}

		return count;
	}

	static void ForceFullCollection()
	{
		for (var i = 0; i < 4; i++)
		{
			GC.Collect();
			GC.WaitForPendingFinalizers();
			GC.Collect();
			Java.Lang.JavaSystem.Gc();
			Java.Lang.JavaSystem.RunFinalization();
			Thread.Sleep(50);
		}
	}

	sealed class PayloadNativeView : AView
	{
		public PayloadNativeView(Android.Content.Context context, int id, LeakPayload payload)
			: base(context)
		{
			PayloadId = id;
			Payload = payload;
			ContentDescription = $"Removed safe-area payload view {id}";
		}

		public int PayloadId { get; }

		public LeakPayload Payload { get; }
	}

	sealed class LeakPayload
	{
		public LeakPayload(int id, int byteCount)
		{
			Id = id;
			Bytes = new byte[byteCount];
			for (var i = 0; i < Bytes.Length; i += 4096)
				Bytes[i] = (byte)((id + i) % 251);
		}

		public int Id { get; }

		public byte[] Bytes { get; }
	}

	public sealed record ScenarioResult(
		string Name,
		int AliveNavigationRoots,
		int AliveInsetListeners,
		int AliveRemovedChildPlatformViews,
		int AlivePayloads)
	{
		public long RetainedPayloadBytes => (long)AlivePayloads * PayloadBytes;
	}

	public sealed record ReproReport(ScenarioResult Control, ScenarioResult Current)
	{
		public bool LeakProved =>
			Control.AliveNavigationRoots == CycleCount &&
			Current.AliveNavigationRoots == CycleCount &&
			Control.AliveInsetListeners == 0 &&
			Control.AlivePayloads == 0 &&
			Control.AliveRemovedChildPlatformViews == 0 &&
			Current.AliveInsetListeners >= CycleCount * 9 / 10 &&
			Current.AliveRemovedChildPlatformViews >= CycleCount * 9 / 10 &&
			Current.AlivePayloads >= CycleCount * 9 / 10;

		public string ToText()
		{
			var builder = new StringBuilder();
			builder.AppendLine("Android FlyoutView local inset listener retention repro");
			builder.AppendLine($"Cycles: {CycleCount}");
			builder.AppendLine($"Payload per removed native child: {PayloadBytes / 1024 / 1024} MiB");
			builder.AppendLine("Retained native roots: FlyoutViewHandler CoordinatorLayout navigation roots only");
			builder.AppendLine($"Leak proved: {LeakProved}");
			builder.AppendLine($"RESULT: {(LeakProved ? "PROVEN" : "NOT PROVEN")}");
			builder.AppendLine();
			AppendScenario(builder, Control);
			builder.AppendLine();
			AppendScenario(builder, Current);
			return builder.ToString();
		}

		static void AppendScenario(StringBuilder builder, ScenarioResult result)
		{
			builder.AppendLine(result.Name);
			builder.AppendLine($"  retained navigation roots: {result.AliveNavigationRoots}/{CycleCount}");
			builder.AppendLine($"  retained inset listeners: {result.AliveInsetListeners}/{CycleCount}");
			builder.AppendLine($"  removed child platform views alive after full GC: {result.AliveRemovedChildPlatformViews}/{CycleCount}");
			builder.AppendLine($"  removed child payloads alive after full GC: {result.AlivePayloads}/{CycleCount}");
			builder.AppendLine($"  retained payload bytes: {result.RetainedPayloadBytes:N0}");
		}
	}
}
