namespace PickerItemsSourceLeakRepro;

public sealed class PickerLeakPage : ContentPage
{
	readonly IReadOnlyList<Picker> _pickers;

	public PickerLeakPage()
	{
		var session = ReproSession.Current ?? throw new InvalidOperationException("No active repro session.");
		var options = session.Options;
		var cycle = session.CurrentCycle;
		var payload = new LeakPayloadViewModel(cycle, options.PayloadBytesPerPage, options.PickersPerPage);

		Title = payload.Title;
		BindingContext = payload;

		var pickers = new List<Picker>(options.PickersPerPage);
		var form = new VerticalStackLayout
		{
			Spacing = 10
		};

		for (var i = 0; i < options.PickersPerPage; i++)
		{
			var picker = new Picker
			{
				Title = $"Route option {i + 1}",
				ItemsSource = session.CreateItemsSource(),
				SelectedIndex = (cycle + i) % options.ChoicesPerPicker,
				BindingContext = payload,
				BackgroundColor = Color.FromArgb("#F6F8FA")
			};

			pickers.Add(picker);
			form.Children.Add(new Label
			{
				Text = $"Routing choice {i + 1}",
				FontSize = 12,
				TextColor = Color.FromArgb("#57606A")
			});
			form.Children.Add(picker);
		}

		_pickers = pickers;
		session.Track(this, _pickers, payload);

		Content = new ScrollView
		{
			Content = new VerticalStackLayout
			{
				Padding = new Thickness(18),
				Spacing = 12,
				Children =
				{
					new Label
					{
						Text = payload.Title,
						FontSize = 22,
						FontAttributes = FontAttributes.Bold,
						TextColor = Color.FromArgb("#0B1F33")
					},
					new Label
					{
						Text = $"{options.Name}: {options.PickersPerPage} pickers, {options.ChoicesPerPicker} choices, {options.PayloadMegabytesPerPage} MB cached payload",
						FontSize = 14,
						TextColor = Color.FromArgb("#57606A")
					},
					form
				}
			}
		};
	}

	protected override void OnDisappearing()
	{
		if (ReproSession.Current?.Options.ClearItemsSourceOnDisappear == true)
		{
			foreach (var picker in _pickers)
				picker.ItemsSource = null;
		}

		base.OnDisappearing();
	}
}
