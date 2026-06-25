namespace ResourceDictionaryMergedDictionariesLeakRepro;

public sealed class LeakPage : ContentPage
{
	public LeakPage()
	{
		var session = ReproSession.Current ?? throw new InvalidOperationException("No active repro session.");
		var options = session.Options;
		var cycle = session.CurrentCycle;
		var payload = new LeakPayloadViewModel(cycle, options.PayloadBytesPerPage);
		var pageResources = session.CreatePageResources(payload);

		Title = payload.Title;
		BindingContext = payload;
		Resources = pageResources;

		var root = new VerticalStackLayout
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
					Text = $"{options.Name}: {options.SharedResourceCount} shared resources, {options.LocalResourceCount} page resources, {options.PayloadMegabytesPerPage} MB cached payload",
					FontSize = 14,
					TextColor = Color.FromArgb("#57606A")
				},
				new Border
				{
					BackgroundColor = Color.FromArgb("#F6F8FA"),
					Stroke = Color.FromArgb("#D0D7DE"),
					StrokeThickness = 1,
					Padding = new Thickness(14),
					Content = new Label
					{
						Text = "The page ResourceDictionary merges a long-lived shared dictionary. The shared dictionary should not keep this page alive after navigation.",
						FontSize = 14,
						TextColor = Color.FromArgb("#172026")
					}
				}
			}
		};

		foreach (var row in payload.Rows.Take(8))
		{
			root.Children.Add(new Label
			{
				Text = $"{row.Id}: {row.Summary}",
				FontSize = 13,
				TextColor = Color.FromArgb("#57606A")
			});
		}

		session.Track(this, pageResources, root, payload);

		Content = new ScrollView
		{
			Content = root
		};
	}

	protected override void OnDisappearing()
	{
		if (ReproSession.Current?.Options.ClearMergedDictionariesOnDisappear == true)
			Resources.MergedDictionaries.Clear();

		base.OnDisappearing();
	}
}
