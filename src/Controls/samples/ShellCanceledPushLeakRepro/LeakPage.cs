namespace ShellCanceledPushLeakRepro;

internal sealed class LeakPage : ContentPage
{
	public LeakPage(LeakPayloadViewModel payload)
	{
		Title = payload.Title;
		BindingContext = payload;

		RootLayout = new VerticalStackLayout
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
					Text = $"This page was passed to Shell Navigation.PushAsync and should be collectable if that push is canceled. Payload: {payload.PayloadBytes / 1024d / 1024d:0.0} MB.",
					FontSize = 14,
					TextColor = Color.FromArgb("#57606A")
				}
			}
		};

		foreach (var row in payload.Rows.Take(8))
		{
			RootLayout.Children.Add(new Label
			{
				Text = $"{row.Id}: {row.Summary}",
				FontSize = 13,
				TextColor = Color.FromArgb("#57606A")
			});
		}

		Content = new ScrollView
		{
			Content = RootLayout
		};
	}

	public VerticalStackLayout RootLayout { get; }
}
