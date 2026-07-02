using Microsoft.Maui.Controls;

namespace VisualDiagnosticsOverlayScrollViewsRetentionRepro;

public sealed class PayloadScrollView : ScrollView
{
	public PayloadScrollView(Payload payload)
	{
		Payload = payload;
		BindingContext = payload;
	}

	public Payload Payload { get; }
}
