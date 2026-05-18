namespace BorderDashArrayLeakRepro;

public sealed class AppShell : Shell
{
	public const string BorderLeakRoute = "border-dash-array-leak-page";

	public AppShell()
	{
		Routing.RegisterRoute(BorderLeakRoute, typeof(BorderLeakPage));

		Items.Add(new ShellContent
		{
			Title = "Border Dash Leak",
			ContentTemplate = new DataTemplate(typeof(DashboardPage))
		});
	}
}
