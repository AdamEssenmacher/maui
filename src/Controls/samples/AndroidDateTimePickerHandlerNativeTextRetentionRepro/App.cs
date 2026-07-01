#nullable enable

using Microsoft.Maui;
using Microsoft.Maui.Controls;

namespace AndroidDateTimePickerHandlerNativeTextRetentionRepro;

public sealed class App : Application
{
	protected override Window CreateWindow(IActivationState? activationState)
	{
		return new Window(new MainPage());
	}
}
