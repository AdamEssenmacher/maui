namespace MauiMemoryLeakRepro
{
public partial class App : Microsoft.Maui.Controls.Application
	{
		public App()
		{
		}

		protected override Window CreateWindow(IActivationState? activationState)
		{
			return new Window(new MainPage());
		}
	}
}
