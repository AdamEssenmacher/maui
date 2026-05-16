namespace MapPoolLeakRepro;

public sealed class AppShell : Shell
{
	public const string MapLeakRoute = "map-leak-page";

	public AppShell()
	{
		Routing.RegisterRoute(MapLeakRoute, typeof(MapLeakPage));

		Items.Add(new ShellContent
		{
			Title = "Map Pool Leak",
			ContentTemplate = new DataTemplate(typeof(DashboardPage))
		});
	}
}
