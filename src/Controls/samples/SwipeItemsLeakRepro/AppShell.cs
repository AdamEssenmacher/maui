namespace SwipeItemsLeakRepro;

public sealed class AppShell : Shell
{
	public const string SwipeLeakRoute = "swipe-items-leak-page";

	public AppShell()
	{
		Routing.RegisterRoute(SwipeLeakRoute, typeof(SwipeLeakPage));

		Items.Add(new ShellContent
		{
			Title = "SwipeItems Leak",
			ContentTemplate = new DataTemplate(typeof(DashboardPage))
		});
	}
}
