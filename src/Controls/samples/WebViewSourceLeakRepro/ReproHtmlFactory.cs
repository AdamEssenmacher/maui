using System.Text;

namespace WebViewSourceLeakRepro;

internal static class ReproHtmlFactory
{
	public static string CreateHtml(int cycle, int targetKilobytes)
	{
		var targetBytes = Math.Max(16, targetKilobytes) * 1024;
		var builder = new StringBuilder(targetBytes + 4096);

		builder.AppendLine("<!doctype html>");
		builder.AppendLine("<html>");
		builder.AppendLine("<head>");
		builder.AppendLine("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\" />");
		builder.AppendLine("<style>");
		builder.AppendLine("body{font-family:-apple-system,BlinkMacSystemFont,'Segoe UI',sans-serif;margin:0;background:#f7f9fb;color:#172026;}");
		builder.AppendLine("header{background:#0f6b5b;color:white;padding:22px 20px;}");
		builder.AppendLine("main{padding:18px 20px 32px;}");
		builder.AppendLine("section{background:white;border:1px solid #d7dee5;border-radius:8px;margin:0 0 14px;padding:14px 16px;}");
		builder.AppendLine("h1{font-size:24px;margin:0 0 6px;}h2{font-size:17px;margin:0 0 8px;}p{line-height:1.45;margin:0 0 10px;}");
		builder.AppendLine(".metric{display:inline-block;margin:6px 8px 0 0;padding:7px 10px;border-radius:16px;background:#e8f3f0;color:#0f4f43;font-weight:600;}");
		builder.AppendLine("</style>");
		builder.AppendLine("</head>");
		builder.AppendLine("<body>");
		builder.AppendLine("<header>");
		builder.AppendLine($"<h1>Customer portal knowledge base - page {cycle + 1}</h1>");
		builder.AppendLine("<p>Offline-ready help content, account summaries, and policy snippets rendered in a WebView.</p>");
		builder.AppendLine("</header>");
		builder.AppendLine("<main>");

		var section = 0;
		while (builder.Length < targetBytes)
		{
			section++;
			builder.AppendLine("<section>");
			builder.AppendLine($"<h2>Case bundle {section:000}</h2>");
			builder.AppendLine("<p>This block represents a realistic generated HTML fragment: formatted support notes, a cached response template, entitlement details, and local asset references prepared by the page view model.</p>");
			builder.AppendLine("<p>Teams often centralize this source object so every WebView can reload when the help article or base URL changes. Reusing that mutable source should not keep closed pages alive.</p>");
			builder.AppendLine($"<span class=\"metric\">cycle {cycle + 1}</span>");
			builder.AppendLine($"<span class=\"metric\">bundle {section}</span>");
			builder.AppendLine("<span class=\"metric\">offline cache</span>");
			builder.AppendLine("</section>");
		}

		builder.AppendLine("</main>");
		builder.AppendLine("</body>");
		builder.AppendLine("</html>");

		return builder.ToString();
	}
}
