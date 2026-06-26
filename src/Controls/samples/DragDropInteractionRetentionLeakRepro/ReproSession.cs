using System.Reflection;
using Foundation;
using Microsoft.Maui.Platform;
using UIKit;

namespace DragDropInteractionRetentionLeakRepro;

public sealed record RunStats(
	string Name,
	int Attempts,
	int AlivePayloads,
	int AlivePlatformArgs,
	int RemainingDragInteractions,
	long RetainedPayloadBytes);

public sealed record ReproReport(
	int Attempts,
	int PayloadBytes,
	RunStats Control,
	RunStats Current,
	long ManagedHeapBaseline,
	long ManagedHeapFinal)
{
	public bool LeakProved =>
		Control.AlivePayloads == 0 &&
		Control.RemainingDragInteractions == 0 &&
		Current.AlivePayloads == Attempts &&
		Current.RemainingDragInteractions == Attempts;

	public string ToText()
	{
		return string.Join(Environment.NewLine,
			"DragDropInteractionRetentionLeakRepro",
			$"Attempts: {Attempts}",
			$"Payload per attempt: {PayloadBytes / 1024 / 1024} MiB",
			$"Leak proved: {LeakProved}",
			string.Empty,
			Format(Control),
			string.Empty,
			Format(Current),
			string.Empty,
			$"Managed heap baseline: {FormatBytes(ManagedHeapBaseline)}",
			$"Managed heap final: {FormatBytes(ManagedHeapFinal)}",
			$"Managed heap delta: {FormatBytes(ManagedHeapFinal - ManagedHeapBaseline)}");
	}

	string Format(RunStats stats)
	{
		return string.Join(Environment.NewLine,
			$"Run: {stats.Name}",
			$"  retained native platform views: {stats.Attempts}",
			$"  retained UIDragInteraction instances: {stats.RemainingDragInteractions}/{stats.Attempts}",
			$"  PlatformDragStartingEventArgs alive after full GC: {stats.AlivePlatformArgs}/{stats.Attempts}",
			$"  drag-start payloads alive after full GC: {stats.AlivePayloads}/{stats.Attempts}",
			$"  retained payload bytes: {FormatBytes(stats.RetainedPayloadBytes)} ({stats.RetainedPayloadBytes * 100.0 / (PayloadBytes * stats.Attempts):0.0}%)");
	}

	static string FormatBytes(long bytes)
	{
		var sign = bytes < 0 ? "-" : "";
		var value = Math.Abs((double)bytes);
		if (value >= 1024 * 1024)
			return $"{sign}{value / 1024 / 1024:0.0} MiB";
		if (value >= 1024)
			return $"{sign}{value / 1024:0.0} KiB";
		return $"{bytes} B";
	}
}

internal static class ReproSession
{
	const int Attempts = 80;
	const int PayloadBytes = 1024 * 1024;

	public static async Task<ReproReport> RunAsync()
	{
		await Task.Yield();

		var mauiContext = Application.Current?.Windows.FirstOrDefault()?.Page?.Handler?.MauiContext
			?? throw new InvalidOperationException("MauiContext is not available.");

		ForceFullGc();
		var baseline = GC.GetTotalMemory(forceFullCollection: true);

		var control = await RunScenarioAsync(
			"control: remove reused drag interactions and clear drag-start args before disposal",
			mauiContext,
			cleanupBeforeDispose: true);

		var current = await RunScenarioAsync(
			"current: reused drag interaction survives GesturePlatformManager disposal",
			mauiContext,
			cleanupBeforeDispose: false);

		ForceFullGc();
		var final = GC.GetTotalMemory(forceFullCollection: true);

		return new ReproReport(Attempts, PayloadBytes, control, current, baseline, final);
	}

	static async Task<RunStats> RunScenarioAsync(string name, IMauiContext mauiContext, bool cleanupBeforeDispose)
	{
		var payloadRefs = new List<WeakReference<Payload>>(Attempts);
		var platformArgsRefs = new List<WeakReference<PlatformDragStartingEventArgs>>(Attempts);
		var retainedPlatformViews = new List<UIView>(Attempts);

		for (var i = 0; i < Attempts; i++)
		{
			CreateDisposedDragView(mauiContext, cleanupBeforeDispose, payloadRefs, platformArgsRefs, retainedPlatformViews, i);

			if (i % 10 == 0)
				await Task.Yield();
		}

		ForceFullGc();

		var alivePayloads = payloadRefs.Count(static wr => wr.TryGetTarget(out _));
		var alivePlatformArgs = platformArgsRefs.Count(static wr => wr.TryGetTarget(out _));
		var dragInteractions = retainedPlatformViews.Sum(static view => view.Interactions.Count(static interaction => interaction is UIDragInteraction));

		return new RunStats(
			name,
			Attempts,
			alivePayloads,
			alivePlatformArgs,
			dragInteractions,
			(long)alivePayloads * PayloadBytes);
	}

	static void CreateDisposedDragView(
		IMauiContext mauiContext,
		bool cleanupBeforeDispose,
		List<WeakReference<Payload>> payloadRefs,
		List<WeakReference<PlatformDragStartingEventArgs>> platformArgsRefs,
		List<UIView> retainedPlatformViews,
		int index)
	{
		var payload = new Payload(index, PayloadBytes);
		payloadRefs.Add(new WeakReference<Payload>(payload));

		var dragRecognizer = CreateDragRecognizer(payload);
		payload = null!;

		var view = new Border
		{
			WidthRequest = 64,
			HeightRequest = 64,
			BackgroundColor = Colors.CornflowerBlue
		};
		view.GestureRecognizers.Add(dragRecognizer);

		var platformView = (UIView)view.ToPlatform(mauiContext);
		var handler = (IPlatformViewHandler)view.Handler!;
		var gesturePlatformManager = GetGesturePlatformManager(view);

		// First load creates the interaction. The second load sees the existing interaction,
		// clears _interactions, and does not add the reused UIDragInteraction back to it.
		InvokeLoadRecognizers(gesturePlatformManager);

		var dragDelegate = GetDragAndDropDelegate(gesturePlatformManager);
		var dragInteraction = platformView.Interactions.OfType<UIDragInteraction>().First();
		var platformArgs = CreatePlatformDragStartingEventArgs(platformView, dragInteraction);

		InvokeHandleDragStarting(dragDelegate, view, handler, platformArgs);
		platformArgsRefs.Add(new WeakReference<PlatformDragStartingEventArgs>(platformArgs));

		if (cleanupBeforeDispose)
		{
			ClearPlatformDragStartingEventArgs(dragDelegate);
			RemoveDragDropInteractions(platformView);
		}

		((IDisposable)gesturePlatformManager).Dispose();

		view.GestureRecognizers.Clear();
		view.Handler?.DisconnectHandler();
		view.Handler = null;

		retainedPlatformViews.Add(platformView);
	}

	static DragGestureRecognizer CreateDragRecognizer(Payload payload)
	{
		var dragRecognizer = new DragGestureRecognizer();
		dragRecognizer.DragStarting += (_, args) =>
		{
			var itemProvider = new NSItemProvider(new NSString($"payload-{payload.Id}"));
			args.PlatformArgs!.SetDragItems(new[] { new UIDragItem(itemProvider) });
			args.PlatformArgs.SetPrefersFullSizePreviews((_, _) => payload.Bytes[0] == payload.Id % 251);
		};
		return dragRecognizer;
	}

	static object GetGesturePlatformManager(View view)
	{
		var gestureManagerField = typeof(View).GetField("_gestureManager", BindingFlags.Instance | BindingFlags.NonPublic)
			?? throw new MissingFieldException(typeof(View).FullName, "_gestureManager");
		var gestureManager = gestureManagerField.GetValue(view)
			?? throw new InvalidOperationException("GestureManager was not created.");
		var property = gestureManager.GetType().GetProperty("GesturePlatformManager", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
			?? throw new MissingMemberException(gestureManager.GetType().FullName, "GesturePlatformManager");
		return property.GetValue(gestureManager)
			?? throw new InvalidOperationException("GesturePlatformManager was not connected.");
	}

	static object GetDragAndDropDelegate(object gesturePlatformManager)
	{
		var field = gesturePlatformManager.GetType().GetField("_dragAndDropDelegate", BindingFlags.Instance | BindingFlags.NonPublic)
			?? throw new MissingFieldException(gesturePlatformManager.GetType().FullName, "_dragAndDropDelegate");
		return field.GetValue(gesturePlatformManager)
			?? throw new InvalidOperationException("DragAndDropDelegate was not created.");
	}

	static PlatformDragStartingEventArgs CreatePlatformDragStartingEventArgs(UIView platformView, UIDragInteraction dragInteraction)
	{
		var ctor = typeof(PlatformDragStartingEventArgs)
			.GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic)
			.Single(static c =>
			{
				var parameters = c.GetParameters();
				return parameters.Length == 3 &&
					parameters[0].ParameterType == typeof(UIView) &&
					parameters[1].ParameterType == typeof(UIDragInteraction);
			});

		return (PlatformDragStartingEventArgs)ctor.Invoke(new object?[] { platformView, dragInteraction, null });
	}

	static void InvokeLoadRecognizers(object gesturePlatformManager)
	{
		var method = gesturePlatformManager.GetType().GetMethod("LoadRecognizers", BindingFlags.Instance | BindingFlags.NonPublic)
			?? throw new MissingMethodException(gesturePlatformManager.GetType().FullName, "LoadRecognizers");
		method.Invoke(gesturePlatformManager, null);
	}

	static void InvokeHandleDragStarting(object dragDelegate, View view, IPlatformViewHandler handler, PlatformDragStartingEventArgs platformArgs)
	{
		var method = dragDelegate.GetType().GetMethod("HandleDragStarting", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
			?? throw new MissingMethodException(dragDelegate.GetType().FullName, "HandleDragStarting");
		method.Invoke(dragDelegate, new object?[] { view, handler, null, platformArgs });
	}

	static void ClearPlatformDragStartingEventArgs(object dragDelegate)
	{
		var field = dragDelegate.GetType().GetField("_platformDragStartingEventArgs", BindingFlags.Instance | BindingFlags.NonPublic)
			?? throw new MissingFieldException(dragDelegate.GetType().FullName, "_platformDragStartingEventArgs");
		field.SetValue(dragDelegate, null);
	}

	static void RemoveDragDropInteractions(UIView platformView)
	{
		foreach (var interaction in platformView.Interactions.ToArray())
		{
			if (interaction is UIDragInteraction or UIDropInteraction)
				platformView.RemoveInteraction(interaction);
		}
	}

	static void ForceFullGc()
	{
		for (var i = 0; i < 4; i++)
		{
			GC.Collect();
			GC.WaitForPendingFinalizers();
			GC.Collect();
		}
	}

	sealed class Payload
	{
		public Payload(int id, int byteCount)
		{
			Id = id;
			Bytes = new byte[byteCount];
			Bytes[0] = (byte)(id % 251);
			Bytes[^1] = (byte)((id + 1) % 251);
		}

		public int Id { get; }

		public byte[] Bytes { get; }
	}
}
