using Microsoft.Maui.Controls.Shapes;

namespace SwipeItemViewCommandLeakRepro;

public sealed class SwipeLeakPage : ContentPage
{
	readonly ReproSession _session;
	readonly List<SwipeItemView> _swipeItemViews = [];

	public SwipeLeakPage()
	{
		_session = ReproSession.Current ?? throw new InvalidOperationException("No active repro session.");

		Title = $"Run {_session.CurrentCycle:N0}";
		BackgroundColor = Color.FromArgb("#F7F8FA");

		var rows = _session.CreateRowsForCurrentCycle();
		var swipeViews = new List<SwipeView>(rows.Count);
		var actionElements = new List<Element>(rows.Count);
		var actionContentViews = new List<View>(rows.Count);

		var list = new VerticalStackLayout
		{
			Padding = new Thickness(14, 12),
			Spacing = 10
		};

		foreach (var row in rows)
		{
			var swipeView = CreateSwipeView(row, actionElements, actionContentViews);
			swipeViews.Add(swipeView);
			list.Children.Add(swipeView);
		}

		_session.Track(this, swipeViews, actionElements, actionContentViews);

		Content = new ScrollView
		{
			Content = list
		};
	}

	protected override void OnDisappearing()
	{
		base.OnDisappearing();

		if (!_session.Options.ClearCommandOnDisappear)
			return;

		foreach (var swipeItemView in _swipeItemViews)
		{
			swipeItemView.Command = null;
			swipeItemView.CommandParameter = null;
			swipeItemView.Content = null;
			swipeItemView.BindingContext = null;
		}
	}

	SwipeView CreateSwipeView(
		DispatchRowViewModel row,
		ICollection<Element> actionElements,
		ICollection<View> actionContentViews)
	{
		var swipeView = new SwipeView
		{
			BindingContext = row,
			Content = CreateRowContent(row),
			RightItems = CreateSwipeItems(row, actionElements, actionContentViews)
		};

		return swipeView;
	}

	SwipeItems CreateSwipeItems(
		DispatchRowViewModel row,
		ICollection<Element> actionElements,
		ICollection<View> actionContentViews)
	{
		var swipeItems = new SwipeItems
		{
			Mode = SwipeMode.Execute,
			SwipeBehaviorOnInvoked = SwipeBehaviorOnInvoked.Close
		};

		if (_session.Options.UseSwipeItemView)
		{
			var content = CreateActionContent(row);
			var action = new SwipeItemView
			{
				BackgroundColor = Color.FromArgb("#B42318"),
				Command = _session.SharedCommand,
				CommandParameter = row,
				Content = content,
				WidthRequest = 132
			};

			_swipeItemViews.Add(action);
			actionElements.Add(action);
			actionContentViews.Add(content);
			swipeItems.Add(action);
		}
		else
		{
			var action = new SwipeItem
			{
				Text = "Close",
				BackgroundColor = Color.FromArgb("#175CD3"),
				Command = _session.SharedCommand,
				CommandParameter = row
			};

			actionElements.Add(action);
			swipeItems.Add(action);
		}

		return swipeItems;
	}

	static View CreateRowContent(DispatchRowViewModel row)
	{
		var title = new Label
		{
			Text = row.Title,
			FontSize = 15,
			FontAttributes = FontAttributes.Bold,
			TextColor = Color.FromArgb("#101828"),
			LineBreakMode = LineBreakMode.TailTruncation
		};

		var subtitle = new Label
		{
			Text = row.Subtitle,
			FontSize = 13,
			TextColor = Color.FromArgb("#475467"),
			LineBreakMode = LineBreakMode.TailTruncation
		};

		var amount = new Label
		{
			Text = row.AmountText,
			FontSize = 15,
			FontAttributes = FontAttributes.Bold,
			TextColor = Color.FromArgb("#067647"),
			HorizontalTextAlignment = TextAlignment.End,
			VerticalTextAlignment = TextAlignment.Center
		};

		var textStack = new VerticalStackLayout
		{
			Spacing = 4,
			Children =
			{
				title,
				subtitle
			}
		};

		var grid = new Grid
		{
			ColumnDefinitions =
			{
				new ColumnDefinition(GridLength.Star),
				new ColumnDefinition(GridLength.Auto)
			},
			ColumnSpacing = 12,
			Children =
			{
				textStack,
				amount
			}
		};

		Grid.SetColumn(amount, 1);

		return new Border
		{
			Stroke = Color.FromArgb("#D0D5DD"),
			StrokeThickness = 1,
			BackgroundColor = Colors.White,
			StrokeShape = new RoundRectangle
			{
				CornerRadius = 8
			},
			Padding = new Thickness(12),
			Content = grid
		};
	}

	static View CreateActionContent(DispatchRowViewModel row)
	{
		return new Grid
		{
			BackgroundColor = Color.FromArgb("#B42318"),
			Children =
			{
				new Label
				{
					Text = "Close",
					FontAttributes = FontAttributes.Bold,
					TextColor = Colors.White,
					HorizontalTextAlignment = TextAlignment.Center,
					VerticalTextAlignment = TextAlignment.Center,
					BindingContext = row
				}
			}
		};
	}
}
