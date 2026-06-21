namespace Maui35154BackRepro;

public sealed class RootBackInterceptPage : ContentPage
{
	readonly Label _status;

	public RootBackInterceptPage()
	{
		Title = "Root Back Intercept Repro";
		_status = new Label
		{
			AutomationId = "StatusLabel",
			Text = "STARTED",
			FontSize = 24,
			HorizontalTextAlignment = TextAlignment.Center
		};

		Content = new Grid
		{
			Padding = 24,
			Children =
			{
				new VerticalStackLayout
				{
					Spacing = 16,
					VerticalOptions = LayoutOptions.Center,
					Children =
					{
						new Label
						{
							Text = "Press Android Back.",
							FontSize = 22,
							HorizontalTextAlignment = TextAlignment.Center
						},
						new Label
						{
							Text = "Expected: this root page handles Back and stays open.",
							HorizontalTextAlignment = TextAlignment.Center
						},
						_status
					}
				}
			}
		};
	}

	protected override bool OnBackButtonPressed()
	{
		BackResult.Write("HANDLED");
		_status.Text = "HANDLED";
		return true;
	}
}
