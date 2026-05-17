namespace SwipeItemsLeakRepro;

internal static class SharedSwipeActionCache
{
	static readonly List<SwipeItems> CachedActionSets = new();
	static readonly Command NoOpCommand = new(() => { });

	public static int CachedSetCount => CachedActionSets.Count;

	public static SwipeItems CreateCachedActionSet(int cycle, int row)
	{
		var items = CreateActionSet(cycle, row);
		CachedActionSets.Add(items);
		return items;
	}

	public static SwipeItems CreateUncachedActionSet(int cycle, int row)
	{
		return CreateActionSet(cycle, row);
	}

	public static void Reset()
	{
		CachedActionSets.Clear();
	}

	static SwipeItems CreateActionSet(int cycle, int row)
	{
		var items = new SwipeItems
		{
			Mode = SwipeMode.Execute,
			SwipeBehaviorOnInvoked = SwipeBehaviorOnInvoked.Close
		};

		items.Add(new SwipeItem
		{
			Text = "Done",
			BackgroundColor = Color.FromArgb("#1A7F64"),
			Command = NoOpCommand,
			CommandParameter = $"{cycle}:{row}:done"
		});
		items.Add(new SwipeItem
		{
			Text = "Route",
			BackgroundColor = Color.FromArgb("#0969DA"),
			Command = NoOpCommand,
			CommandParameter = $"{cycle}:{row}:route"
		});
		items.Add(new SwipeItem
		{
			Text = "Hold",
			BackgroundColor = Color.FromArgb("#BF8700"),
			Command = NoOpCommand,
			CommandParameter = $"{cycle}:{row}:hold"
		});

		return items;
	}
}
