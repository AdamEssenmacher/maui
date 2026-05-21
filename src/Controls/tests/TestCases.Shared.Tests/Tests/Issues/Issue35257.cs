#if IOS
using NUnit.Framework;
using UITest.Appium;
using UITest.Core;

namespace Microsoft.Maui.TestCases.Tests.Issues;

public class Issue35257 : _IssuesUITest
{
	public override string Issue => "Switch custom colors render on iOS 26";

	public Issue35257(TestDevice device) : base(device)
	{
	}

	[Test]
	[Category(UITestCategories.Switch)]
	public void SwitchCustomColorsRenderOnInitialState()
	{
		App.WaitForElement("CustomOffSwitch", timeout: TimeSpan.FromSeconds(60));
		App.WaitForElement("DefaultOffSwitch");
		App.WaitForElement("CustomOnSwitch");
		App.WaitForElement("DefaultOnSwitch");

		VerifyScreenshot();
	}
}
#endif
