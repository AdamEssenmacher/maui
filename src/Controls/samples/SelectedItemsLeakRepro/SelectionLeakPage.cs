using System.Globalization;

namespace SelectedItemsLeakRepro;

public sealed class SelectionLeakPage : ContentPage
{
	public SelectionLeakPage()
	{
		var session = ReproSession.Current ?? throw new InvalidOperationException("Start a repro run from the dashboard first.");
		var options = session.Options;
		var cycle = session.CurrentCycle;
		var customers = CustomerFactory.Create(cycle, options.RowsPerPage);
		var selectedState = SelectionStateStore.CreateSelection(cycle, customers, options.SelectedItemsPerPage, options);
		var viewModel = new CustomerSelectionViewModel(cycle, customers, selectedState, options.PayloadBytesPerPage);
		var collectionView = CreateCollectionView(viewModel);
		var header = CreateHeader(viewModel, options);
		var footer = CreateFooter(options);

		Title = viewModel.Title;
		BindingContext = viewModel;

		var layout = new Grid
		{
			RowDefinitions =
			{
				new RowDefinition(GridLength.Auto),
				new RowDefinition(GridLength.Star),
				new RowDefinition(GridLength.Auto)
			}
		};

		layout.Add(header, 0, 0);
		layout.Add(collectionView, 0, 1);
		layout.Add(footer, 0, 2);

		Content = layout;

		Grid.SetRow(collectionView, 1);

		session.Track(this, collectionView, viewModel, collectionView.SelectedItems, selectedState);
	}

	static CollectionView CreateCollectionView(CustomerSelectionViewModel viewModel)
	{
		var collectionView = new CollectionView
		{
			BindingContext = viewModel,
			SelectionMode = SelectionMode.Multiple,
			ItemSizingStrategy = ItemSizingStrategy.MeasureFirstItem,
			ItemTemplate = new DataTemplate(CreateCustomerRow)
		};

		collectionView.SetBinding(ItemsView.ItemsSourceProperty, nameof(CustomerSelectionViewModel.Customers));
		collectionView.SetBinding(SelectableItemsView.SelectedItemsProperty, nameof(CustomerSelectionViewModel.SelectedCustomers));

		return collectionView;
	}

	static View CreateHeader(CustomerSelectionViewModel viewModel, ReproOptions options)
	{
		var grid = new Grid
		{
			Padding = new Thickness(16, 14),
			BackgroundColor = Color.FromArgb("#0B6B55"),
			ColumnDefinitions =
			{
				new ColumnDefinition(GridLength.Star),
				new ColumnDefinition(GridLength.Auto)
			},
			RowDefinitions =
			{
				new RowDefinition(GridLength.Auto),
				new RowDefinition(GridLength.Auto)
			}
		};

		grid.Add(new Label
		{
			Text = viewModel.Title,
			FontSize = 18,
			FontAttributes = FontAttributes.Bold,
			TextColor = Colors.White
		}, 0, 0);

		grid.Add(new Label
		{
			Text = options.SelectionStateKind,
			FontSize = 12,
			TextColor = Color.FromArgb("#D8F3EA"),
			HorizontalTextAlignment = TextAlignment.End
		}, 1, 0);

		grid.Add(new Label
		{
			Text = string.Create(CultureInfo.InvariantCulture, $"{options.RowsPerPage} customers / {options.SelectedItemsPerPage} selected / {options.PayloadMegabytesPerPage} MB page payload"),
			FontSize = 12,
			TextColor = Color.FromArgb("#D8F3EA")
		}, 0, 1);

		return grid;
	}

	static View CreateFooter(ReproOptions options)
	{
		return new Label
		{
			Padding = new Thickness(16, 10),
			FontSize = 12,
			TextColor = Color.FromArgb("#304256"),
			BackgroundColor = Color.FromArgb("#EEF4F3"),
			Text = options.RetainSelectionState
				? "Selected state is retained by an app-level workflow store."
				: "Selected state is scoped to this page."
		};
	}

	static View CreateCustomerRow()
	{
		var name = new Label
		{
			FontSize = 14,
			FontAttributes = FontAttributes.Bold,
			TextColor = Color.FromArgb("#0B1F33")
		};
		name.SetBinding(Label.TextProperty, nameof(CustomerRecord.DisplayName));

		var detail = new Label
		{
			FontSize = 12,
			TextColor = Color.FromArgb("#52677A")
		};
		detail.SetBinding(Label.TextProperty, nameof(CustomerRecord.Detail));

		var status = new Label
		{
			FontSize = 12,
			TextColor = Color.FromArgb("#0B6B55"),
			HorizontalTextAlignment = TextAlignment.End
		};
		status.SetBinding(Label.TextProperty, nameof(CustomerRecord.Status));

		var value = new Label
		{
			FontSize = 13,
			FontAttributes = FontAttributes.Bold,
			TextColor = Color.FromArgb("#23374A"),
			HorizontalTextAlignment = TextAlignment.End
		};
		value.SetBinding(Label.TextProperty, nameof(CustomerRecord.ValueLabel));

		var grid = new Grid
		{
			Padding = new Thickness(14, 10),
			ColumnDefinitions =
			{
				new ColumnDefinition(GridLength.Star),
				new ColumnDefinition(new GridLength(130))
			},
			RowDefinitions =
			{
				new RowDefinition(GridLength.Auto),
				new RowDefinition(GridLength.Auto)
			},
			ColumnSpacing = 12,
			RowSpacing = 3
		};

		grid.Add(name, 0, 0);
		grid.Add(value, 1, 0);
		grid.Add(detail, 0, 1);
		grid.Add(status, 1, 1);

		return grid;
	}
}
