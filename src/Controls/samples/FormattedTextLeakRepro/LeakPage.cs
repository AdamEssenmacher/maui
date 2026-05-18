namespace FormattedTextLeakRepro;

public sealed class LeakPage : ContentPage
{
	readonly List<Label> _disclosureLabels = new();

	public LeakPage()
	{
		var session = ReproSession.Current ?? throw new InvalidOperationException("No active repro session.");
		var options = session.Options;
		var pageNumber = session.CurrentPage;
		var rows = new List<DisclosureRowViewModel>(options.DisclosuresPerPage);

		Title = $"Checkout {pageNumber + 1}";
		BackgroundColor = Color.FromArgb("#F8FAFC");

		var stack = new VerticalStackLayout
		{
			Padding = new Thickness(14, 14, 14, 24),
			Spacing = 10
		};

		stack.Children.Add(new Label
		{
			Text = $"Checkout review {pageNumber + 1}",
			FontSize = 22,
			FontAttributes = FontAttributes.Bold,
			TextColor = Color.FromArgb("#0F172A")
		});

		stack.Children.Add(new Label
		{
			Text = "Each account card uses the same rich disclosure text that a real app would often keep in Application.Resources.",
			FontSize = 13,
			TextColor = Color.FromArgb("#475569")
		});

		for (var rowIndex = 0; rowIndex < options.DisclosuresPerPage; rowIndex++)
		{
			var row = new DisclosureRowViewModel(pageNumber, rowIndex, options.PayloadBytesPerDisclosure);
			rows.Add(row);
			stack.Children.Add(CreateAccountCard(row, rowIndex, options));
		}

		Content = new ScrollView { Content = stack };
		session.Track(this, _disclosureLabels, rows);
	}

	protected override void OnDisappearing()
	{
		if (ReproSession.Current?.Options.ClearFormattedTextOnDisappear == true)
		{
			foreach (var label in _disclosureLabels)
				label.FormattedText = null;
		}

		base.OnDisappearing();
	}

	View CreateAccountCard(DisclosureRowViewModel row, int rowIndex, ReproOptions options)
	{
		var title = new Label
		{
			Text = row.Title,
			FontSize = 15,
			FontAttributes = FontAttributes.Bold,
			TextColor = Color.FromArgb("#0F172A")
		};

		var detail = new Label
		{
			Text = "Priority review, saved payment method, loyalty profile, shipping rules, and fraud-screening details loaded.",
			FontSize = 12,
			TextColor = Color.FromArgb("#64748B")
		};

		var disclosure = new Label
		{
			BindingContext = row,
			FormattedText = options.UseSharedFormattedText
				? RichTextCatalog.GetShared(rowIndex)
				: RichTextCatalog.CreateInline(rowIndex),
			FontSize = 12,
			LineBreakMode = LineBreakMode.WordWrap,
			TextColor = Color.FromArgb("#334155")
		};

		_disclosureLabels.Add(disclosure);

		return new VerticalStackLayout
		{
			Spacing = 4,
			Padding = new Thickness(12),
			BackgroundColor = Colors.White,
			Children =
			{
				title,
				detail,
				disclosure
			}
		};
	}
}
