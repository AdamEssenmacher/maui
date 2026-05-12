using System.Diagnostics;
using System.Reflection;
using Microsoft.Maui.Platform;

namespace Maui.Controls.Sample.AndroidWebViewFileChooserCallbackLeakRepro;

public static class LeakTracker
{
	static readonly List<WeakReference> TrackedCallbacks = new();
	static readonly FieldInfo? ActivityResultCallbacksField =
		typeof(ActivityResultCallbackRegistry).GetField("ActivityResultCallbacks", BindingFlags.NonPublic | BindingFlags.Static);

	public static int CreatedCallbackCount;
	public static int CompletedCallbackCount;
	public static int FinalizedCallbackCount;

	public static void Track(object instance)
	{
		TrackedCallbacks.Add(new WeakReference(instance));
	}

	public static LeakSnapshot Snapshot()
	{
		var aliveWeakReferences = 0;

		foreach (var weakReference in TrackedCallbacks)
		{
			if (weakReference.IsAlive)
				aliveWeakReferences++;
		}

		return new LeakSnapshot(
			GetRegistryCallbackCount(),
			CreatedCallbackCount,
			CompletedCallbackCount,
			FinalizedCallbackCount,
			CreatedCallbackCount - FinalizedCallbackCount,
			aliveWeakReferences,
			TrackedCallbacks.Count);
	}

	public static LeakSnapshot CollectAndSnapshot()
	{
		GC.Collect();
		GC.WaitForPendingFinalizers();
		GC.Collect();

		var snapshot = Snapshot();
		Debug.WriteLine(
			$"WebViewFileChooserCallbackLeakRepro Registry={snapshot.RegistryCallbackCount}, " +
			$"Created={snapshot.CreatedCallbackCount}, Completed={snapshot.CompletedCallbackCount}, " +
			$"Finalized={snapshot.FinalizedCallbackCount}, Live={snapshot.LiveTrackedCallbackCount}, " +
			$"WeakAlive={snapshot.AliveWeakReferences}/{snapshot.TotalWeakReferences}");

		return snapshot;
	}

	static int GetRegistryCallbackCount()
	{
		var callbacks = ActivityResultCallbacksField?.GetValue(null);
		var countProperty = callbacks?.GetType().GetProperty("Count");

		return countProperty?.GetValue(callbacks) is int count
			? count
			: -1;
	}
}

public readonly record struct LeakSnapshot(
	int RegistryCallbackCount,
	int CreatedCallbackCount,
	int CompletedCallbackCount,
	int FinalizedCallbackCount,
	int LiveTrackedCallbackCount,
	int AliveWeakReferences,
	int TotalWeakReferences);
