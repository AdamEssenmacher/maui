namespace Maui.Controls.HybridWebViewRepro.Platform;

class Program : MauiApplication
{
	protected override MauiApp CreateMauiApp()
	{
		return MauiProgram.CreateMauiApp();
	}

	static void Main(string[] args)
	{
		var app = new Program();
		app.Run(args);
	}
}
