namespace SwipeItemsLeakRepro;

public sealed class SwipeLeakPage : ContentPage
{
	readonly List<SwipeView> _swipeViews = new();

	public SwipeLeakPage()
	{
		var session = ReproSession.Current ?? throw new InvalidOperationException("No active repro session.");
		var options = session.Options;
		var cycle = session.CurrentCycle;
		var board = new WorkOrderBoardViewModel(cycle, options.RowsPerPage, options.PayloadBytesPerRow);
		var swipeItems = new List<SwipeItems>();

		Title = board.Title;
		BindingContext = board;
		BackgroundColor = Color.FromArgb("#F6F8FA");

		var rows = new VerticalStackLayout
		{
			Spacing = 8
		};

		foreach (var row in board.Rows)
		{
			var rowItems = options.CacheSwipeItems
				? SharedSwipeActionCache.CreateCachedActionSet(cycle, row.Row)
				: SharedSwipeActionCache.CreateUncachedActionSet(cycle, row.Row);

			var swipeView = new SwipeView
			{
				BindingContext = board,
				RightItems = rowItems,
				Content = CreateRowCard(row)
			};

			_swipeViews.Add(swipeView);
			swipeItems.Add(rowItems);
			rows.Add(swipeView);
		}

		session.Track(this, board, _swipeViews, swipeItems, board.Rows);

		Content = new ScrollView
		{
			Content = new VerticalStackLayout
			{
				Padding = new Thickness(16, 16, 16, 28),
				Spacing = 14,
				Children =
				{
					CreateHeader(board, options),
					rows
				}
			}
		};
	}

	protected override void OnDisappearing()
	{
		if (ReproSession.Current?.Options.ReplaceRightItemsOnDisappear == true)
		{
			foreach (var swipeView in _swipeViews)
				swipeView.RightItems = new SwipeItems();
		}

		base.OnDisappearing();
	}

	static View CreateHeader(WorkOrderBoardViewModel board, ReproOptions options)
	{
		return new Border
		{
			StrokeThickness = 1,
			Stroke = Color.FromArgb("#D0D7DE"),
			BackgroundColor = Colors.White,
			Padding = new Thickness(14),
			Content = new VerticalStackLayout
			{
				Spacing = 4,
				Children =
				{
					new Label
					{
						Text = board.Title,
						FontSize = 20,
						FontAttributes = FontAttributes.Bold,
						TextColor = Color.FromArgb("#24292F")
					},
					new Label
					{
						Text = $"{options.Name}: {board.Rows.Count} swipe rows, {FormatBytes(board.PayloadBytes)} cached row payload",
						FontSize = 13,
						TextColor = Color.FromArgb("#57606A")
					}
				}
			}
		};
	}

	static View CreateRowCard(WorkOrderRowViewModel row)
	{
		var title = new Label
		{
			Text = row.WorkOrder,
			FontSize = 15,
			FontAttributes = FontAttributes.Bold,
			TextColor = Color.FromArgb("#24292F")
		};

		var summary = new Label
		{
			Text = row.Summary,
			FontSize = 13,
			TextColor = Color.FromArgb("#57606A")
		};

		var badge = new Label
		{
			Text = row.Row % 4 == 0 ? "SLA" : row.Row % 4 == 1 ? "Parts" : row.Row % 4 == 2 ? "Route" : "Ready",
			FontSize = 12,
			TextColor = Colors.White,
			BackgroundColor = row.Row % 4 == 0 ? Color.FromArgb("#CF222E") : Color.FromArgb("#57606A"),
			Padding = new Thickness(8, 3),
			HorizontalTextAlignment = TextAlignment.Center,
			VerticalTextAlignment = TextAlignment.Center
		};

		var grid = new Grid
		{
			ColumnDefinitions =
			{
				new ColumnDefinition(GridLength.Star),
				new ColumnDefinition(GridLength.Auto)
			},
			RowDefinitions =
			{
				new RowDefinition(GridLength.Auto),
				new RowDefinition(GridLength.Auto)
			},
			ColumnSpacing = 12,
			RowSpacing = 3,
			BindingContext = row
		};

		grid.Add(title, 0, 0);
		grid.Add(summary, 0, 1);
		grid.Add(badge, 1, 0);
		Grid.SetRowSpan(badge, 2);

		return new Border
		{
			StrokeThickness = 1,
			Stroke = Color.FromArgb("#D0D7DE"),
			BackgroundColor = Colors.White,
			Padding = new Thickness(12),
			Content = grid
		};
	}

	static string FormatBytes(long bytes)
	{
		if (bytes >= 1024L * 1024L)
			return $"{bytes / 1024d / 1024d:0.0} MB";

		if (bytes >= 1024L)
			return $"{bytes / 1024d:0.0} KB";

		return $"{bytes} B";
	}
}
