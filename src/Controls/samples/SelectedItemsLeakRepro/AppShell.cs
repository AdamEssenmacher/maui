namespace SelectedItemsLeakRepro;

public sealed class AppShell : Shell
{
	public const string SelectionLeakRoute = "selection-leak-page";

	public AppShell()
	{
		Routing.RegisterRoute(SelectionLeakRoute, typeof(SelectionLeakPage));

		Items.Add(new ShellContent
		{
			Title = "SelectedItems Leak",
			ContentTemplate = new DataTemplate(typeof(DashboardPage))
		});
	}
}
