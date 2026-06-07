namespace ShapeCollectionResetLeakRepro;

public sealed class AppShell : Shell
{
	public const string ShapeLeakRoute = "shape-leak-page";

	public AppShell()
	{
		Routing.RegisterRoute(ShapeLeakRoute, typeof(ShapeLeakPage));

		Items.Add(new ShellContent
		{
			Title = "Shape Reset Leak",
			ContentTemplate = new DataTemplate(typeof(DashboardPage))
		});
	}
}
