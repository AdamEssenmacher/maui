namespace CarouselViewDetachedRefreshRepro;

public sealed class App : Application
{
	protected override Window CreateWindow(IActivationState? activationState)
	{
		var page = new CatalogPage(new CatalogViewModel());
		return new Window(new NavigationPage(page)
		{
			BarBackgroundColor = Color.FromArgb("#203864"),
			BarTextColor = Colors.White
		});
	}
}
