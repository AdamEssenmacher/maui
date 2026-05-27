namespace MapElementsPerfRepro;

public sealed class AppShell : Shell
{
	public const string MapStressRoute = "map-stress-page";

	public AppShell()
	{
		Routing.RegisterRoute(MapStressRoute, typeof(MapStressPage));

		Items.Add(new ShellContent
		{
			Title = "MapElements Perf",
			ContentTemplate = new DataTemplate(typeof(DashboardPage))
		});
	}
}
