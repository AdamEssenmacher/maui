namespace AndroidWebViewRefreshRepro;

public sealed class MainPage : ContentPage
{
	readonly Label _refreshCountLabel;
	readonly RefreshView _refreshView;
	readonly WebView _webView;
	int _refreshCount;

	public MainPage()
	{
		Title = "Android WebView RefreshView Repro";
		BackgroundColor = Colors.White;

		_refreshCountLabel = new Label
		{
			AutomationId = "RefreshCountLabel",
			FontAttributes = FontAttributes.Bold,
			FontSize = 18,
			TextColor = Color.FromArgb("#1F2937")
		};

		var resetButton = new Button
		{
			AutomationId = "ResetButton",
			Text = "Reset",
			HorizontalOptions = LayoutOptions.End,
			VerticalOptions = LayoutOptions.Center
		};
		resetButton.Clicked += async (_, _) => await ResetAsync();

		var header = new Grid
		{
			Padding = new Thickness(16, 12),
			ColumnDefinitions =
			{
				new ColumnDefinition(GridLength.Star),
				new ColumnDefinition(GridLength.Auto)
			}
		};
		header.Add(_refreshCountLabel);
		header.Add(resetButton, 1);

		_webView = new WebView
		{
			AutomationId = "ReproWebView",
			Source = new HtmlWebViewSource { Html = WebViewHtml }
		};

		_refreshView = new RefreshView
		{
			AutomationId = "RefreshRoot",
			Content = _webView
		};
		_refreshView.Refreshing += OnRefreshing;

		Content = new Grid
		{
			RowDefinitions =
			{
				new RowDefinition(GridLength.Auto),
				new RowDefinition(GridLength.Star)
			},
			Children =
			{
				header,
				_refreshView
			}
		};
		Grid.SetRow(_refreshView, 1);

		UpdateRefreshCountLabel();
	}

	void OnRefreshing(object? sender, EventArgs e)
	{
		_refreshCount++;
		UpdateRefreshCountLabel();
		_refreshView.IsRefreshing = false;
	}

	async Task ResetAsync()
	{
		_refreshCount = 0;
		UpdateRefreshCountLabel();
		_refreshView.IsRefreshing = false;

		try
		{
			await _webView.EvaluateJavaScriptAsync("window.resetReproScrollState && window.resetReproScrollState(); 'ok';");
		}
		catch
		{
			// The WebView may still be initializing; the initial page position is already at the top.
		}
	}

	void UpdateRefreshCountLabel()
	{
		_refreshCountLabel.Text = $"Refresh count: {_refreshCount}";
	}

	const string WebViewHtml = """
		<!doctype html>
		<html>
		<head>
			<meta name="viewport" content="width=device-width, initial-scale=1">
			<style>
				html, body {
					margin: 0;
					padding: 0;
					background: #ffffff;
					color: #111827;
					font-family: sans-serif;
					font-size: 18px;
					height: 100%;
					overflow: hidden;
				}

				.top {
					padding: 24px 18px;
					background: #eef4ff;
					border-bottom: 1px solid #c8d7f5;
				}

				.top strong {
					display: block;
					margin-bottom: 8px;
					font-size: 22px;
				}

				.content {
					padding: 18px;
				}

				.inner-scroll {
					height: 820px;
					overflow-y: scroll;
					padding: 16px;
					border: 1px solid #d1d5db;
					background: #f9fafb;
				}

				.row {
					height: 120px;
					border-bottom: 1px solid #d1d5db;
					display: flex;
					align-items: center;
				}
			</style>
			<script>
				window.resetReproScrollState = function () {
					window.scrollTo(0, 0);
					var innerScroller = document.getElementById('innerScroller');
					if (innerScroller) {
						innerScroller.scrollTop = 620;
					}
				};
				window.addEventListener('load', function () {
					window.setTimeout(window.resetReproScrollState, 50);
				});
			</script>
		</head>
		<body>
			<section class="top">
				<strong>Nested WebView scroller</strong>
				The inner list starts scrolled down. Pull down inside it. A working RefreshView must not refresh until this inner list reaches the top.
			</section>
			<section class="content">
				<div id="innerScroller" class="inner-scroll">
					<div class="row">Inner row 1</div>
					<div class="row">Inner row 2</div>
					<div class="row">Inner row 3</div>
					<div class="row">Inner row 4</div>
					<div class="row">Inner row 5</div>
					<div class="row">Inner row 6</div>
					<div class="row">Inner row 7</div>
					<div class="row">Inner row 8</div>
					<div class="row">Inner row 9</div>
					<div class="row">Inner row 10</div>
					<div class="row">Inner row 11</div>
					<div class="row">Inner row 12</div>
				</div>
			</section>
		</body>
		</html>
		""";
}
