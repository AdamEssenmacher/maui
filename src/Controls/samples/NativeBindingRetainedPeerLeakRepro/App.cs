using Microsoft.Maui.Controls;

namespace NativeBindingRetainedPeerLeakRepro;

public sealed class App : Application
{
	bool _started;

	protected override Window CreateWindow(IActivationState? activationState)
	{
		var window = new Window(new ContentPage
		{
			Content = new Label
			{
				Text = "Running NativeBinding retained-peer leak repro...",
				HorizontalOptions = LayoutOptions.Center,
				VerticalOptions = LayoutOptions.Center
			}
		});

		window.Created += (_, _) =>
		{
			if (_started)
				return;

			_started = true;
			window.Dispatcher.Dispatch(async () =>
			{
				var proved = false;

				try
				{
					proved = await ReproSession.RunAsync();
				}
				finally
				{
					Environment.Exit(proved ? 0 : 2);
				}
			});
		};

		return window;
	}
}
