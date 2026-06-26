namespace VisualElementResourcesLeakRepro;

public sealed class AppShell : Shell
{
	public const string LeakRoute = "visual-element-resources-leak-page";

	public AppShell()
	{
		Routing.RegisterRoute(LeakRoute, typeof(LeakPage));

		Items.Add(new ShellContent
		{
			Title = "Resources Leak",
			ContentTemplate = new DataTemplate(typeof(DashboardPage))
		});
	}
}
