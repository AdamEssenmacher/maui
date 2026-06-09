using System.ComponentModel;
using System.Runtime.CompilerServices;
using Maui.Controls.Sample;

namespace Maui.Controls.Sample.Issues;

[Issue(IssueTracker.None, 0, "CollectionView MeasureFirstItem visual proof", PlatformAffected.iOS | PlatformAffected.macOS | PlatformAffected.Android)]
public class CollectionViewMeasureFirstItemVisualProof : ContentPage
{
	readonly Label _summaryLabel;
	readonly CollectionView1 _legacyCollectionView;
	readonly CollectionView2 _items2CollectionView;

	public CollectionViewMeasureFirstItemVisualProof()
	{
		Title = "MeasureFirstItem visual proof";
		BackgroundColor = Colors.White;
		MeasureFirstItemProbeRegistry.Reset();
		MeasureFirstItemProbeRegistry.MeasurementsChanged += OnMeasurementsChanged;

		_summaryLabel = new Label
		{
			AutomationId = "MeasureFirstItemProofSummary",
			FontSize = 13,
			TextColor = Colors.Black,
			Margin = new Thickness(12, 8, 12, 4)
		};

		var resetButton = new Button
		{
			Text = "Reset",
			AutomationId = "MeasureFirstItemProofReset"
		};
		resetButton.Clicked += (_, _) => ResetProof();

		var scrollButton = new Button
		{
			Text = "Scroll to 40",
			AutomationId = "MeasureFirstItemProofScroll"
		};
		scrollButton.Clicked += (_, _) => ScrollBoth();

		var buttonGrid = new Grid
		{
			Margin = new Thickness(12, 0, 12, 8),
			ColumnSpacing = 8,
			ColumnDefinitions =
			{
				new ColumnDefinition { Width = GridLength.Star },
				new ColumnDefinition { Width = GridLength.Star }
			}
		};
		buttonGrid.Add(resetButton, 0, 0);
		buttonGrid.Add(scrollButton, 1, 0);

		_legacyCollectionView = CreateCollectionView<CollectionView1>("Legacy CV1");
		_items2CollectionView = CreateCollectionView<CollectionView2>("Items2 CV2");

		var root = new Grid
		{
			RowSpacing = 0,
			RowDefinitions =
			{
				new RowDefinition { Height = GridLength.Auto },
				new RowDefinition { Height = GridLength.Auto },
				new RowDefinition { Height = GridLength.Star }
			}
		};

		root.Add(new VerticalStackLayout
		{
			Children =
			{
				new Label
				{
					Text = "Green = first item measured. Blue = non-first measured with cached first-item height. Amber = non-first measured without cached first-item height. Purple = both.",
					AutomationId = "MeasureFirstItemProofLegend",
					FontAttributes = FontAttributes.Bold,
					FontSize = 14,
					TextColor = Colors.Black,
					Margin = new Thickness(12, 12, 12, 2)
				},
				_summaryLabel
			}
		}, 0, 0);

		root.Add(buttonGrid, 0, 1);

		var comparisonGrid = new Grid
		{
			Margin = new Thickness(12, 0, 12, 10),
			ColumnSpacing = 8,
			ColumnDefinitions =
			{
				new ColumnDefinition { Width = GridLength.Star },
				new ColumnDefinition { Width = GridLength.Star }
			}
		};

		comparisonGrid.Add(CreateSection("Legacy CV1", _legacyCollectionView), 0, 0);
		comparisonGrid.Add(CreateSection("Items2 CV2", _items2CollectionView), 1, 0);

		root.Add(comparisonGrid, 0, 2);

		Content = root;
		UpdateSummary();
	}

	protected override void OnDisappearing()
	{
		MeasureFirstItemProbeRegistry.MeasurementsChanged -= OnMeasurementsChanged;
		base.OnDisappearing();
	}

	static TCollectionView CreateCollectionView<TCollectionView>(string handlerName)
		where TCollectionView : CollectionView, new()
	{
		return new TCollectionView
		{
			AutomationId = $"{handlerName.Replace(" ", string.Empty, StringComparison.Ordinal)}CollectionView",
			ItemSizingStrategy = ItemSizingStrategy.MeasureFirstItem,
			ItemsLayout = new LinearItemsLayout(ItemsLayoutOrientation.Vertical)
			{
				ItemSpacing = 4
			},
			ItemTemplate = CreateTemplate(),
			ItemsSource = CreateItems(handlerName)
		};
	}

	static DataTemplate CreateTemplate()
	{
		return new DataTemplate(() => new MeasureFirstItemProbeCell());
	}

	static List<MeasureFirstItemProbeItem> CreateItems(string handlerName)
	{
		var items = new List<MeasureFirstItemProbeItem>();
		for (int n = 0; n < 80; n++)
		{
			items.Add(new MeasureFirstItemProbeItem(handlerName, n));
		}

		return items;
	}

	static View CreateSection(string title, CollectionView collectionView)
	{
		var section = new Grid
		{
			RowDefinitions =
			{
				new RowDefinition { Height = GridLength.Auto },
				new RowDefinition { Height = GridLength.Star }
			}
		};

		section.Add(new Label
		{
			Text = title,
			FontAttributes = FontAttributes.Bold,
			TextColor = Colors.Black,
			AutomationId = $"{title.Replace(" ", string.Empty, StringComparison.Ordinal)}Title"
		}, 0, 0);

		section.Add(collectionView, 0, 1);
		return section;
	}

	void ResetProof()
	{
		MeasureFirstItemProbeRegistry.Reset();

		_legacyCollectionView.ItemsSource = null;
		_items2CollectionView.ItemsSource = null;

		_legacyCollectionView.ItemTemplate = CreateTemplate();
		_items2CollectionView.ItemTemplate = CreateTemplate();

		_legacyCollectionView.ItemsSource = CreateItems("Legacy CV1");
		_items2CollectionView.ItemsSource = CreateItems("Items2 CV2");

		_legacyCollectionView.ScrollTo(0, position: ScrollToPosition.Start, animate: false);
		_items2CollectionView.ScrollTo(0, position: ScrollToPosition.Start, animate: false);

		UpdateSummary();
	}

	void ScrollBoth()
	{
		_legacyCollectionView.ScrollTo(40, position: ScrollToPosition.Start, animate: false);
		_items2CollectionView.ScrollTo(40, position: ScrollToPosition.Start, animate: false);
	}

	void OnMeasurementsChanged()
	{
		Dispatcher.Dispatch(UpdateSummary);
	}

	void UpdateSummary()
	{
		_summaryLabel.Text = MeasureFirstItemProbeRegistry.GetSummary();
	}
}

public sealed class MeasureFirstItemProbeItem
	: INotifyPropertyChanged
{
	public MeasureFirstItemProbeItem(string handlerName, int index)
	{
		HandlerName = handlerName;
		Index = index;
		Title = $"Item {Index}";
	}

	public event PropertyChangedEventHandler PropertyChanged;

	public string HandlerName { get; }

	public int Index { get; }

	public string Key => $"{HandlerName}:{Index}";

	public string Title { get; }

	public Color StateColor { get; private set; } = Color.FromArgb("#E5E7EB");

	public string StateText { get; private set; } = "not measured";

	internal void ApplyMeasurementState(MeasureFirstItemProbeState state, int measureCount)
	{
		var stateColor = GetStateColor(state);
		var stateText = GetStateText(state, measureCount);

		if (StateColor != stateColor)
		{
			StateColor = stateColor;
			OnPropertyChanged(nameof(StateColor));
		}

		if (StateText != stateText)
		{
			StateText = stateText;
			OnPropertyChanged(nameof(StateText));
		}
	}

	static Color GetStateColor(MeasureFirstItemProbeState state)
	{
		return state switch
		{
			MeasureFirstItemProbeState.FirstMeasured => Color.FromArgb("#BBF7D0"),
			MeasureFirstItemProbeState.CachedHeightMeasured => Color.FromArgb("#BFDBFE"),
			MeasureFirstItemProbeState.UncachedMeasure => Color.FromArgb("#FDE68A"),
			MeasureFirstItemProbeState.BothCachedAndUncached => Color.FromArgb("#DDD6FE"),
			_ => Color.FromArgb("#E5E7EB")
		};
	}

	static string GetStateText(MeasureFirstItemProbeState state, int measureCount)
	{
		var measureText = measureCount == 0
			? string.Empty
			: $" ({measureCount})";

		return state switch
		{
			MeasureFirstItemProbeState.FirstMeasured => $"first{measureText}",
			MeasureFirstItemProbeState.CachedHeightMeasured => $"cached{measureText}",
			MeasureFirstItemProbeState.UncachedMeasure => $"uncached{measureText}",
			MeasureFirstItemProbeState.BothCachedAndUncached => $"both{measureText}",
			MeasureFirstItemProbeState.Unbound => "unbound",
			_ => "not measured"
		};
	}

	void OnPropertyChanged([CallerMemberName] string propertyName = null)
	{
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
	}
}

public sealed class MeasureFirstItemProbeCell : Grid
{
	MeasureFirstItemProbeItem _item;

	public MeasureFirstItemProbeCell()
	{
		Padding = new Thickness(12, 8);
		ColumnSpacing = 8;
		BackgroundColor = Color.FromArgb("#E5E7EB");

		ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Star });
		ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

		var titleLabel = new Label
		{
			FontSize = 14,
			TextColor = Colors.Black,
			VerticalOptions = LayoutOptions.Center
		};
		titleLabel.SetBinding(Label.TextProperty, nameof(MeasureFirstItemProbeItem.Title));

		var stateLabel = new Label
		{
			FontSize = 12,
			TextColor = Colors.Black,
			VerticalOptions = LayoutOptions.Center
		};
		stateLabel.SetBinding(Label.TextProperty, nameof(MeasureFirstItemProbeItem.StateText));
		SetBinding(BackgroundColorProperty, new Binding(nameof(MeasureFirstItemProbeItem.StateColor)));

		Children.Add(titleLabel);
		Children.Add(stateLabel);
		Grid.SetColumn(stateLabel, 1);
	}

	protected override void OnBindingContextChanged()
	{
		base.OnBindingContextChanged();
		_item = BindingContext as MeasureFirstItemProbeItem;
	}

	protected override Size MeasureOverride(double widthConstraint, double heightConstraint)
	{
		var size = base.MeasureOverride(widthConstraint, heightConstraint);

		if (_item is not null)
		{
			MeasureFirstItemProbeRegistry.RecordMeasurement(_item, widthConstraint, heightConstraint, size);
		}

		return size;
	}
}

enum MeasureFirstItemProbeState
{
	Unbound,
	NotMeasured,
	FirstMeasured,
	CachedHeightMeasured,
	UncachedMeasure,
	BothCachedAndUncached
}

static class MeasureFirstItemProbeRegistry
{
	static readonly object Lock = new();
	static readonly Dictionary<string, MeasureFirstItemProbeRecord> Records = new();

	public static event Action MeasurementsChanged;

	public static void Reset()
	{
		lock (Lock)
		{
			Records.Clear();
		}

		MeasurementsChanged?.Invoke();
	}

	public static void RecordMeasurement(MeasureFirstItemProbeItem item, double widthConstraint, double heightConstraint, Size measuredSize)
	{
		List<MeasureFirstItemProbeSnapshot> snapshots;
		var shouldUpdate = false;

		lock (Lock)
		{
			if (!Records.TryGetValue(item.Key, out var record))
			{
				record = new MeasureFirstItemProbeRecord(item);
				Records[item.Key] = record;
			}

			record.MeasureCount++;
			record.HeightConstraints.Add(heightConstraint);
			if (item.Index == 0 && record.MeasuredHeight <= 0 && measuredSize.Height > 0)
			{
				record.MeasuredHeight = measuredSize.Height;
			}

			snapshots = CreateChangedSnapshots(item.HandlerName);
			shouldUpdate = snapshots.Count > 0;
		}

		foreach (var snapshot in snapshots)
		{
			snapshot.Item.ApplyMeasurementState(snapshot.State, snapshot.MeasureCount);
		}

		if (shouldUpdate)
		{
			MeasurementsChanged?.Invoke();
		}
	}

	static List<MeasureFirstItemProbeSnapshot> CreateChangedSnapshots(string handlerName)
	{
		var snapshots = new List<MeasureFirstItemProbeSnapshot>();

		foreach (var record in Records.Values)
		{
			if (record.HandlerName != handlerName)
			{
				continue;
			}

			var state = GetProbeState(record);
			if (record.LastState == state && record.LastAppliedMeasureCount == record.MeasureCount)
			{
				continue;
			}

			record.LastState = state;
			record.LastAppliedMeasureCount = record.MeasureCount;
			snapshots.Add(new MeasureFirstItemProbeSnapshot(record.Item, state, record.MeasureCount));
		}

		return snapshots;
	}

	public static MeasureFirstItemProbeState GetProbeState(MeasureFirstItemProbeItem item)
	{
		lock (Lock)
		{
			if (!Records.TryGetValue(item.Key, out var record))
			{
				return MeasureFirstItemProbeState.NotMeasured;
			}

			return GetProbeState(record);
		}
	}

	public static string GetSummary()
	{
		lock (Lock)
		{
			return string.Join(Environment.NewLine,
				CreateSummary("Legacy CV1"),
				CreateSummary("Items2 CV2"));
		}
	}

	static string CreateSummary(string handlerName)
	{
		var cachedHeightMeasuredNonFirst = new List<int>();
		var uncachedMeasuredNonFirst = new List<int>();
		var bothMeasuredNonFirst = new List<int>();

		foreach (var record in Records.Values)
		{
			if (record.HandlerName != handlerName || record.Index == 0 || record.MeasureCount == 0)
			{
				continue;
			}

			var state = GetProbeState(record);
			if (state == MeasureFirstItemProbeState.CachedHeightMeasured)
			{
				cachedHeightMeasuredNonFirst.Add(record.Index);
			}
			else if (state == MeasureFirstItemProbeState.UncachedMeasure)
			{
				uncachedMeasuredNonFirst.Add(record.Index);
			}
			else if (state == MeasureFirstItemProbeState.BothCachedAndUncached)
			{
				bothMeasuredNonFirst.Add(record.Index);
			}
		}

		cachedHeightMeasuredNonFirst.Sort();
		uncachedMeasuredNonFirst.Sort();
		bothMeasuredNonFirst.Sort();

		var cachedPreview = CreatePreview(cachedHeightMeasuredNonFirst);
		var uncachedPreview = CreatePreview(uncachedMeasuredNonFirst);
		var bothPreview = CreatePreview(bothMeasuredNonFirst);

		return $"{handlerName}: {cachedHeightMeasuredNonFirst.Count} cached-height non-first ({cachedPreview}); {uncachedMeasuredNonFirst.Count} uncached non-first ({uncachedPreview}); {bothMeasuredNonFirst.Count} both ({bothPreview})";
	}

	static string CreatePreview(List<int> indexes)
	{
		var preview = indexes.Count == 0
			? "none"
			: string.Join(", ", indexes.Take(12));

		if (indexes.Count > 12)
		{
			preview += ", ...";
		}

		return preview;
	}

	static MeasureFirstItemProbeState GetProbeState(MeasureFirstItemProbeRecord record)
	{
		if (record.MeasureCount == 0)
		{
			return MeasureFirstItemProbeState.NotMeasured;
		}

		if (record.Index == 0)
		{
			return MeasureFirstItemProbeState.FirstMeasured;
		}

		var firstHeight = GetFirstMeasuredHeight(record.HandlerName);
		var hasCachedMeasure = HasCachedHeightMeasure(record, firstHeight);
		var hasUncachedMeasure = HasUncachedMeasure(record, firstHeight);

		if (hasCachedMeasure && hasUncachedMeasure)
		{
			return MeasureFirstItemProbeState.BothCachedAndUncached;
		}

		return hasCachedMeasure
			? MeasureFirstItemProbeState.CachedHeightMeasured
			: MeasureFirstItemProbeState.UncachedMeasure;
	}

	static double GetFirstMeasuredHeight(string handlerName)
	{
		foreach (var record in Records.Values)
		{
			if (record.HandlerName == handlerName && record.Index == 0 && record.MeasuredHeight > 0)
			{
				return record.MeasuredHeight;
			}
		}

		return 0;
	}

	static bool HasCachedHeightMeasure(MeasureFirstItemProbeRecord record, double firstHeight)
	{
		if (firstHeight <= 0)
		{
			return false;
		}

		foreach (var heightConstraint in record.HeightConstraints)
		{
			if (IsCachedHeightConstraint(heightConstraint, firstHeight))
			{
				return true;
			}
		}

		return false;
	}

	static bool HasUncachedMeasure(MeasureFirstItemProbeRecord record, double firstHeight)
	{
		foreach (var heightConstraint in record.HeightConstraints)
		{
			if (!IsCachedHeightConstraint(heightConstraint, firstHeight))
			{
				return true;
			}
		}

		return false;
	}

	static bool IsCachedHeightConstraint(double heightConstraint, double firstHeight)
	{
		return firstHeight > 0
			&& !double.IsInfinity(heightConstraint)
			&& Math.Abs(heightConstraint - firstHeight) < 0.5;
	}

	sealed class MeasureFirstItemProbeRecord
	{
		public MeasureFirstItemProbeRecord(MeasureFirstItemProbeItem item)
		{
			Item = item;
		}

		public MeasureFirstItemProbeItem Item { get; }

		public string HandlerName => Item.HandlerName;

		public int Index => Item.Index;

		public int MeasureCount { get; set; }

		public double MeasuredHeight { get; set; }

		public MeasureFirstItemProbeState LastState { get; set; } = MeasureFirstItemProbeState.NotMeasured;

		public int LastAppliedMeasureCount { get; set; }

		public List<double> HeightConstraints { get; } = new();
	}

	readonly record struct MeasureFirstItemProbeSnapshot(
		MeasureFirstItemProbeItem Item,
		MeasureFirstItemProbeState State,
		int MeasureCount);
}
