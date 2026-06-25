namespace ShellCanceledPushLeakRepro;

public sealed class AppShell : Shell
{
	public const string DashboardRoute = "dashboard";
	public const string CleanupRoute = "cleanup-target";

	public AppShell()
	{
		Items.Add(new ShellContent
		{
			Route = DashboardRoute,
			Title = "Canceled Push Leak",
			ContentTemplate = new DataTemplate(typeof(DashboardPage))
		});

		Items.Add(new ShellContent
		{
			Route = CleanupRoute,
			Title = "Cleanup",
			ContentTemplate = new DataTemplate(typeof(CleanupPage))
		});
	}
}
