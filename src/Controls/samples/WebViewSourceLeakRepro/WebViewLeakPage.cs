namespace WebViewSourceLeakRepro;

public sealed class WebViewLeakPage : ContentPage
{
	readonly WebView _webView;

	public WebViewLeakPage()
	{
		var session = ReproSession.Current ?? throw new InvalidOperationException("No active repro session.");
		var options = session.Options;
		var cycle = session.CurrentCycle;
		var payload = new LeakPayloadViewModel(cycle, options.PayloadBytesPerPage);
		var source = session.CreateSourceForCurrentCycle();

		Title = payload.Title;
		BindingContext = payload;

		_webView = new WebView
		{
			Source = source,
			BindingContext = payload,
			HeightRequest = 420
		};

		session.Track(this, _webView, payload);

		var footer = new Label
		{
			Text = $"{options.Name}: cycle {cycle + 1}, payload {options.PayloadMegabytesPerPage} MB, HTML {options.HtmlKilobytes} KB",
			Margin = new Thickness(12),
			FontSize = 13,
			TextColor = Colors.White,
			BackgroundColor = Color.FromArgb("#8C000000")
		};
		Grid.SetRow(footer, 1);

		var layout = new Grid
		{
			RowDefinitions =
			{
				new RowDefinition(GridLength.Star),
				new RowDefinition(GridLength.Auto)
			}
		};

		layout.Add(_webView);
		layout.Add(footer);
		Content = layout;
	}

	protected override void OnDisappearing()
	{
		if (ReproSession.Current?.Options.ClearSourceOnDisappear == true)
			_webView.Source = null;

		base.OnDisappearing();
	}
}
