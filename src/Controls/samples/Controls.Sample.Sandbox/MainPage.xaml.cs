#if ANDROID
using AColor = Android.Graphics.Color;
using ABitmap = Android.Graphics.Bitmap;
using ACanvas = Android.Graphics.Canvas;
using ATextView = Android.Widget.TextView;
#endif

namespace Maui.Controls.Sample;

public partial class MainPage : ContentPage
{
	readonly SwipeItem _swipeItem;
	readonly SwipeView _swipeView;
	readonly Label _statusLabel;
	bool _isDark;

	public MainPage()
	{
		InitializeComponent();

		BackgroundColor = Color.FromArgb("#F3F3F3");

		_swipeItem = new SwipeItem
		{
			AutomationId = "TintSwipeItem",
			Text = "ACTION",
			IconImageSource = new FontImageSource
			{
				FontFamily = "Ionicons",
				Glyph = "\uf30c",
				Size = 42
			},
			BackgroundColor = Colors.White
		};

		var swipeItems = new SwipeItems
		{
			Mode = SwipeMode.Reveal
		};
		swipeItems.Add(_swipeItem);

		_swipeView = new SwipeView
		{
			AutomationId = "TintSwipeView",
			HeightRequest = 180,
			RightItems = swipeItems,
			Content = new Grid
			{
				BackgroundColor = Color.FromArgb("#2F80ED"),
				Children =
				{
					new Label
					{
						Text = "The action is opened programmatically →",
						TextColor = Colors.White,
						FontSize = 18,
						HorizontalOptions = LayoutOptions.Center,
						VerticalOptions = LayoutOptions.Center
					}
				}
			}
		};

		_statusLabel = new Label
		{
			AutomationId = "TintStatus",
			FontFamily = "monospace",
			FontSize = 16,
			LineBreakMode = LineBreakMode.WordWrap,
			Text = "Waiting for SwipeItem handler..."
		};
		_statusLabel.TextColor = Colors.Black;

		var lightButton = CreateButton("1. Reset light", "LightButton", async () =>
		{
			_isDark = false;
			_swipeItem.BackgroundColor = Colors.White;
			await OpenAndInspectAsync("WHITE / reset background");
		});

		var darkButton = CreateButton("2. Set SwipeItem background black", "DarkButton", async () =>
		{
			_isDark = true;
			_swipeItem.BackgroundColor = Colors.Black;
			await InspectWithoutReopeningAsync("BLACK / BackgroundColor changed only");
		});

		var forceButton = CreateButton("3. Force icon Source remap", "ForceSourceButton", async () =>
		{
			_swipeItem.Handler?.UpdateValue(nameof(Microsoft.Maui.IMenuElement.Source));
			await InspectWithoutReopeningAsync("BLACK / after explicit Source remap");
		});

		var inspectButton = CreateButton("Inspect current native colors", "InspectButton", async () =>
		{
			await OpenAndInspectAsync("MANUAL INSPECTION");
		});

		Content = new ScrollView
		{
			Content = new VerticalStackLayout
			{
				Padding = 24,
				Spacing = 14,
				Children =
				{
					new Label
					{
						Text = "SwipeItem implicit FontImageSource tint repro",
						FontSize = 24,
						FontAttributes = FontAttributes.Bold
					},
					new Label
					{
						Text = "Expected: white action background → black icon; black action background → white icon.",
						FontSize = 15
					},
					_swipeView,
					_statusLabel,
					lightButton,
					darkButton,
					forceButton,
					inspectButton
				}
			}
		};

		Loaded += async (_, _) => await OpenAndInspectAsync("LIGHT / initial");
	}

	static Button CreateButton(string text, string automationId, Func<Task> action)
	{
		var button = new Button
		{
			Text = text,
			AutomationId = automationId
		};
		button.Clicked += async (_, _) => await action();
		return button;
	}

	async Task OpenAndInspectAsync(string phase)
	{
		await Task.Delay(350);
		_swipeView.Open(OpenSwipeItem.RightItems, false);
		await Task.Delay(350);
		_statusLabel.Text = GetNativeColorReport(phase);
	}

	async Task InspectWithoutReopeningAsync(string phase)
	{
		await Task.Delay(350);
		_statusLabel.Text = GetNativeColorReport(phase);
	}

	string GetNativeColorReport(string phase)
	{
#if ANDROID
		if (_swipeItem.Handler?.PlatformView is not ATextView textView)
			return $"{phase}\nERROR: SwipeItem native TextView is not connected.";

		var drawables = textView.GetCompoundDrawables();
		var drawable = drawables.Length > 1 ? drawables[1] : null;
		if (drawable is null)
			return $"{phase}\nERROR: top icon drawable is missing.";

		var oldLeft = drawable.Bounds.Left;
		var oldTop = drawable.Bounds.Top;
		var oldRight = drawable.Bounds.Right;
		var oldBottom = drawable.Bounds.Bottom;
		const int size = 96;
		using var bitmap = ABitmap.CreateBitmap(size, size, ABitmap.Config.Argb8888!);
		using var canvas = new ACanvas(bitmap);
		bitmap.EraseColor(AColor.Transparent);
		drawable.SetBounds(0, 0, size, size);
		drawable.Draw(canvas);
		drawable.SetBounds(oldLeft, oldTop, oldRight, oldBottom);

		long red = 0;
		long green = 0;
		long blue = 0;
		long weight = 0;
		for (var y = 0; y < size; y++)
		{
			for (var x = 0; x < size; x++)
			{
				var pixel = bitmap.GetPixel(x, y);
				var alpha = AColor.GetAlphaComponent(pixel);
				if (alpha < 32)
					continue;

				red += AColor.GetRedComponent(pixel) * alpha;
				green += AColor.GetGreenComponent(pixel) * alpha;
				blue += AColor.GetBlueComponent(pixel) * alpha;
				weight += alpha;
			}
		}

		if (weight == 0)
			return $"{phase}\nERROR: rendered icon had no visible pixels.";

		var r = (int)(red / weight);
		var g = (int)(green / weight);
		var b = (int)(blue / weight);
		var luma = (int)(0.2126 * r + 0.7152 * g + 0.0722 * b);
		var expected = _isDark ? "WHITE" : "BLACK";
		var observed = luma >= 160 ? "WHITE" : luma <= 95 ? "BLACK" : "MID";
		var verdict = expected == observed ? "MATCH" : "STALE / REGRESSION";
		var nativeText = textView.CurrentTextColor;

		return $"{phase}\n" +
			$"SwipeItem background={(_isDark ? "#000000" : "#FFFFFF")}\n" +
			$"expected icon={expected}\n" +
			$"native icon=#{r:X2}{g:X2}{b:X2} luma={luma} → {observed}\n" +
			$"native text=#{AColor.GetRedComponent(nativeText):X2}{AColor.GetGreenComponent(nativeText):X2}{AColor.GetBlueComponent(nativeText):X2}\n" +
			$"RESULT: {verdict}";
#else
		return $"{phase}\nNative color inspection is implemented for Android.";
#endif
	}
}
