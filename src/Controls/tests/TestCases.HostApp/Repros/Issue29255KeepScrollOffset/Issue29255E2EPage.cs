using System.Collections.ObjectModel;

namespace Maui.Controls.Sample;

public sealed class Issue29255E2EPage : ContentPage
{
	readonly ObservableCollection<string> _items = NewItems("A");
	readonly CollectionView _collectionView;
	readonly Label _status;

	public Issue29255E2EPage()
	{
		Title = "MAUI #29255 E2E";

		_status = new Label
		{
			AutomationId = "StatusLabel",
			FontSize = 18,
			TextColor = Colors.Black,
			Padding = new Thickness(12, 8),
			Text = "Preparing..."
		};

		_collectionView = new CollectionView
		{
			AutomationId = "CollectionView",
			ItemsSource = _items,
			ItemsUpdatingScrollMode = ItemsUpdatingScrollMode.KeepScrollOffset,
			ItemTemplate = new DataTemplate(() =>
			{
				var label = new Label
				{
					FontSize = 18,
					TextColor = Colors.Black,
					Padding = new Thickness(12),
					BackgroundColor = Color.FromArgb("#f7f7f7")
				};

				label.SetBinding(Label.TextProperty, ".");
				return label;
			})
		};

		var rerun = new Button
		{
			Text = "Run MAUI #29255 repro",
			AutomationId = "RunRepro"
		};
		rerun.Clicked += (_, _) => RunRepro();

		Content = new Grid
		{
			BackgroundColor = Colors.White,
			RowDefinitions =
			{
				new RowDefinition(GridLength.Auto),
				new RowDefinition(GridLength.Star),
				new RowDefinition(GridLength.Auto)
			},
			Children =
			{
				_status,
				_collectionView,
				rerun
			}
		};

		Grid.SetRow(_collectionView, 1);
		Grid.SetRow(rerun, 2);

		Loaded += (_, _) => Dispatcher.DispatchDelayed(TimeSpan.FromMilliseconds(500), RunRepro);
	}

	void RunRepro()
	{
		_status.Text = "1. KeepScrollOffset is set; replacing ItemsSource...";

		var newItems = NewItems("B");
		_collectionView.ItemsSource = newItems;
		_collectionView.ScrollTo(0, position: ScrollToPosition.Start, animate: false);

		Dispatcher.DispatchDelayed(TimeSpan.FromMilliseconds(500), () =>
		{
			_status.Text = "2. Insert NEW 1 at index 0...";
			newItems.Insert(0, "NEW 1");

			Dispatcher.DispatchDelayed(TimeSpan.FromMilliseconds(1000), () =>
			{
				_status.Text =
					"If #29255 regresses, top row stays B item 01 and NEW 1 is hidden above. " +
					"If fixed, NEW 1 is visible at the top.";
			});
		});
	}

	static ObservableCollection<string> NewItems(string prefix) =>
		new(Enumerable.Range(1, 80).Select(i => $"{prefix} item {i:00}"));
}
