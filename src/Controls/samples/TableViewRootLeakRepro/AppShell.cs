namespace TableViewRootLeakRepro;

public sealed class AppShell : Shell
{
	public const string LeakRoute = "tableview-root-leak-page";

	public AppShell()
	{
		Routing.RegisterRoute(LeakRoute, typeof(LeakPage));
		Items.Add(new ShellContent
		{
			Title = "TableViewRoot Leak",
			ContentTemplate = new DataTemplate(typeof(DashboardPage))
		});
	}
}
