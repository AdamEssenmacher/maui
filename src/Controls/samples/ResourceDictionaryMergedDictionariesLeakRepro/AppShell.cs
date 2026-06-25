namespace ResourceDictionaryMergedDictionariesLeakRepro;

public sealed class AppShell : Shell
{
	public const string LeakRoute = "resource-dictionary-leak-page";

	public AppShell()
	{
		Routing.RegisterRoute(LeakRoute, typeof(LeakPage));

		Items.Add(new ShellContent
		{
			Title = "ResourceDictionary Leak",
			ContentTemplate = new DataTemplate(typeof(DashboardPage))
		});
	}
}
