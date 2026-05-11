using System.Diagnostics;

namespace Maui.Controls.Sample.AndroidStaleContainerViewLeakRepro;

public static class LeakTracker
{
	static readonly List<WeakReference> TrackedObjects = new();

	public static int RootFlyoutCount;
	public static int FlyoutPageCount;
	public static int DetailNavigationPageCount;
	public static int DetailContentPageCount;

	public static void Track(object instance)
	{
		TrackedObjects.Add(new WeakReference(instance));
	}

	public static LeakSnapshot Snapshot()
	{
		var aliveWeakReferences = 0;

		foreach (var weakReference in TrackedObjects)
		{
			if (weakReference.IsAlive)
				aliveWeakReferences++;
		}

		return new LeakSnapshot(
			RootFlyoutCount,
			FlyoutPageCount,
			DetailNavigationPageCount,
			DetailContentPageCount,
			aliveWeakReferences,
			TrackedObjects.Count);
	}

	public static LeakSnapshot CollectAndSnapshot()
	{
		GC.Collect();
		GC.WaitForPendingFinalizers();
		GC.Collect();

		var snapshot = Snapshot();
		Debug.WriteLine(
			$"StaleContainerViewLeakRepro Root={snapshot.RootFlyoutCount}, Flyout={snapshot.FlyoutPageCount}, " +
			$"DetailNavigation={snapshot.DetailNavigationPageCount}, DetailContent={snapshot.DetailContentPageCount}, " +
			$"WeakAlive={snapshot.AliveWeakReferences}/{snapshot.TotalWeakReferences}");

		return snapshot;
	}
}

public readonly record struct LeakSnapshot(
	int RootFlyoutCount,
	int FlyoutPageCount,
	int DetailNavigationPageCount,
	int DetailContentPageCount,
	int AliveWeakReferences,
	int TotalWeakReferences);
