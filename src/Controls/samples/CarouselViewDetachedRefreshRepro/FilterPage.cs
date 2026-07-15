namespace CarouselViewDetachedRefreshRepro;

public sealed class FilterPage : ContentPage
{
	readonly CatalogPage _catalogPage;
	bool _started;

	public FilterPage(CatalogPage catalogPage)
	{
		_catalogPage = catalogPage;
		Title = "Travel filter";
		BackgroundColor = Color.FromArgb("#F5F7FA");

		Content = new VerticalStackLayout
		{
			Padding = new Thickness(28, 56),
			Spacing = 22,
			Children =
			{
				new Label
				{
					Text = "Applying “Travel essentials”…",
					FontSize = 26,
					FontAttributes = FontAttributes.Bold,
					TextColor = Color.FromArgb("#172B4D")
				},
				new ActivityIndicator
				{
					IsRunning = true,
					Color = Color.FromArgb("#0052CC"),
					WidthRequest = 48,
					HeightRequest = 48
				},
				new Label
				{
					Text = "The shared ViewModel receives a replacement catalog and selects its recommended product while the previous page is off-screen.",
					FontSize = 16,
					TextColor = Color.FromArgb("#42526E")
				}
			}
		};
	}

	protected override async void OnAppearing()
	{
		base.OnAppearing();
		if (_started)
			return;

		_started = true;
		try
		{
			var detached = await WaitForAsync(() => _catalogPage.IsNativeCarouselDetached, TimeSpan.FromSeconds(5));
			_catalogPage.RecordDetachedRefresh(detached);
			await Task.Delay(500);
			_catalogPage.PrepareForReturn();
			await Navigation.PopAsync(animated: true);
		}
		catch (Exception exception)
		{
			Console.WriteLine($"CAROUSEL_REPRO_FILTER_ERROR|{exception}");
			_catalogPage.RecordFilterError(exception);
			_catalogPage.PrepareForReturn();
			await Navigation.PopAsync(animated: false);
		}
	}

	static async Task<bool> WaitForAsync(Func<bool> condition, TimeSpan timeout)
	{
		var deadline = DateTimeOffset.UtcNow + timeout;
		while (DateTimeOffset.UtcNow < deadline)
		{
			if (condition())
				return true;

			await Task.Delay(100);
		}

		return condition();
	}
}
