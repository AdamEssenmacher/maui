namespace MapElementsSyncPerfRepro;

public sealed class AppShell : Shell
{
	public const string SyncStressRoute = "sync-stress-page";

	public AppShell()
	{
		Routing.RegisterRoute(SyncStressRoute, typeof(SyncStressPage));

		Items.Add(new ShellContent
		{
			Title = "MapElements Sync",
			ContentTemplate = new DataTemplate(typeof(DashboardPage))
		});
	}
}
