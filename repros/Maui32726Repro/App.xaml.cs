using Microsoft.Extensions.DependencyInjection;

namespace Maui32726Repro;

public partial class App : Application
{
	public App()
	{
		InitializeComponent();
	}

	protected override Window CreateWindow(IActivationState? activationState)
	{
		return new Window(new MainPage());
	}
}
