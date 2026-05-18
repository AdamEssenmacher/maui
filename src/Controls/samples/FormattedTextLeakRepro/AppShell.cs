namespace FormattedTextLeakRepro;

public sealed class AppShell : Shell
{
	public AppShell()
	{
		Items.Add(new ShellContent
		{
			Title = "FormattedText Leak",
			ContentTemplate = new DataTemplate(typeof(DashboardPage))
		});
	}
}
