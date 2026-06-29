using Android.App;
using Android.Runtime;

namespace AndroidTabbedPageTabTitleRetentionRepro;

[Application]
public sealed class MainApplication : Application
{
	public MainApplication(nint handle, JniHandleOwnership ownership)
		: base(handle, ownership)
	{
	}
}
