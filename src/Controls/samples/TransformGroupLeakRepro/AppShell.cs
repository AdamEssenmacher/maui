namespace TransformGroupLeakRepro;

public sealed class AppShell : Shell
{
	public const string TransformLeakRoute = "transform-leak-page";

	public AppShell()
	{
		Routing.RegisterRoute(TransformLeakRoute, typeof(TransformLeakPage));

		Items.Add(new ShellContent
		{
			Title = "TransformGroup Leak",
			ContentTemplate = new DataTemplate(typeof(DashboardPage))
		});
	}
}
