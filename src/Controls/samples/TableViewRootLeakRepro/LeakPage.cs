namespace TableViewRootLeakRepro;

public sealed class LeakPage : ContentPage
{
	readonly TableView _tableView;

	public LeakPage()
	{
		var session = ReproSession.Current ?? throw new InvalidOperationException("No active repro session.");
		var options = session.Options;
		var payload = new LeakPayloadViewModel(session.CurrentCycle, options.PayloadBytesPerPage);

		Title = payload.Title;
		BindingContext = payload;

		_tableView = new TableView
		{
			Root = session.CreateTableRoot(),
			Intent = TableIntent.Settings,
			BindingContext = payload
		};

		session.Track(this, _tableView, payload);

		Content = new VerticalStackLayout
		{
			Children =
			{
				new Label
				{
					Text = $"{options.Name}: payload {options.PayloadMegabytesPerPage} MB",
					Margin = new Thickness(12),
					FontSize = 14,
					TextColor = Color.FromArgb("#57606A")
				},
				_tableView
			}
		};
	}

	protected override void OnDisappearing()
	{
		if (ReproSession.Current?.Options.ClearRootOnDisappear == true)
			_tableView.Root = null;

		base.OnDisappearing();
	}
}
