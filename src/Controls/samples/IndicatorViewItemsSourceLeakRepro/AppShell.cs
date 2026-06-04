namespace IndicatorViewItemsSourceLeakRepro;

public sealed class AppShell : Shell
{
	public const string LeakRoute = "indicator-feed-page";

	public AppShell()
	{
		Routing.RegisterRoute(LeakRoute, typeof(IndicatorFeedPage));

		Items.Add(new ShellContent
		{
			Title = "IndicatorView Leak",
			ContentTemplate = new DataTemplate(typeof(DashboardPage))
		});
	}
}
