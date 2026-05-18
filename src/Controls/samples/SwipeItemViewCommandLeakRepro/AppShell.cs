namespace SwipeItemViewCommandLeakRepro;

public sealed class AppShell : Shell
{
	public const string SwipeLeakRoute = "swipe-item-view-command-leak-page";

	public AppShell()
	{
		Routing.RegisterRoute(SwipeLeakRoute, typeof(SwipeLeakPage));

		Items.Add(new ShellContent
		{
			Title = "SwipeItemView Command Leak",
			ContentTemplate = new DataTemplate(typeof(DashboardPage))
		});
	}
}
