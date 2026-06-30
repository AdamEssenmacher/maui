using Android.App;
using Android.Runtime;

namespace AndroidShellFlyoutContentRendererDisconnectContextRetentionRepro;

[Application]
public sealed class MainApplication : Application
{
	public MainApplication(nint handle, JniHandleOwnership ownership)
		: base(handle, ownership)
	{
	}
}
