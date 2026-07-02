using Microsoft.Maui.Controls;

namespace XamlTransientNameScopeDetachedChildRetentionRepro;

public partial class DetachedChildPage : ContentPage
{
	public DetachedChildPage(Payload payload)
	{
		Payload = payload;
		InitializeComponent();
	}

	public Payload Payload { get; }

	public Label DetachNamedChild()
	{
		RootGrid.Remove(DetachedChild);
		Content = null;

		if (DetachedChild.Parent is not null || DetachedChild.RealParent is not null)
			throw new InvalidOperationException("The child was not detached from its XAML parent.");

		return DetachedChild;
	}
}
