namespace IndicatorViewTemplateSwapLeakRepro;

public sealed class AppShell : Shell
{
	public AppShell()
	{
		Items.Add(new ShellContent
		{
			Title = "IndicatorView Template Swap Leak",
			ContentTemplate = new DataTemplate(typeof(DashboardPage))
		});
	}
}
