namespace Maui.Controls.Sample;

public static partial class MauiProgram
{
	static partial void OverrideMainPage(ref Page mainPage)
	{
		mainPage = new Issue29255E2EPage();
	}
}
