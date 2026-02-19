using AngleSharp.Dom;
using AngleSharp.Html.Parser;

namespace AIOMarketMaker.Tests.Common;

public static class PageBuilder
{
    public static IDocument BuildProductPage(
        string? id = null,
        string? title = null,
        decimal? price = null,
        string? currencySymbol = null,
        decimal? shippingCost = null,
        string? conditionText = null,
        IEnumerable<string>? imageUrls = null,
        string? itemSpecificsHtml = null,
        string? descriptionUrl = null,
        string? buyBoxText = null,
        string? statusMessage = null,
        string? locationText = null
    )
    {
        var parts = new List<string>();
        parts.Add("<!DOCTYPE html>");
        parts.Add("<html><body>");

        if (id != null)
            parts.Add($@"<div class=""ux-layout-section__textual-display--itemId"">Item Id: {id}</div>");

        if (title != null)
            parts.Add($@"<h1 class=""x-item-title__mainTitle"">{title}</h1>");

        if (price != null)
        {
            var sym = currencySymbol ?? "";
            parts.Add($@"<div class=""x-price-primary"">{sym}{price}</div>");
        }

        if (shippingCost != null)
        {
            var sym = currencySymbol ?? "";
            // include location span only if locationText != null
            var locSpan = locationText != null
                ? $@"<span class=""ux-textspans--SECONDARY"">{locationText}</span>": "";
            parts.Add($@"<div class=""ux-labels-values--shipping"">
                            <div></div>
                            <div><div><div>
                              <span>{sym}{shippingCost}</span>
                            </div></div></div>
                            {locSpan}
                          </div>".Trim());
        }

        if (conditionText != null)
            parts.Add($@"<div class=""x-item-condition-text"">
                          <span class=""ux-textspans"">{conditionText}</span>
                        </div>");

        if (imageUrls != null)
        {
            var imgs = string.Concat(imageUrls.Select(src => $@"<img src=""{src}""/>"));
            parts.Add($@"<div class=""ux-image-grid no-scrollbar"">
                          {imgs}
                        </div>");
        }

        if (itemSpecificsHtml != null)
            parts.Add($@"<div class=""ux-layout-section-evo ux-layout-section--features"">
                          {itemSpecificsHtml}
                        </div>");

        if (descriptionUrl != null)
            parts.Add($@"<iframe id=""desc_ifr"" src=""{descriptionUrl}""></iframe>");

        if (buyBoxText != null)
            parts.Add($@"<div class=""x-buybox-cta"">{buyBoxText}</div>");

        if (statusMessage != null)
            parts.Add($@"<div class=""d-statusmessage"">{statusMessage}</div>");

        parts.Add("</body></html>");

        var html = string.Join(Environment.NewLine, parts);
        var parser = new HtmlParser();
        return parser.ParseDocument(html);
    }

    public static IDocument BuildEmptyDocument()
    {
        var html = string.Join(Environment.NewLine, "");
        var parser = new HtmlParser();
        return parser.ParseDocument(html);
    }

    public static IDocument LoadTestHtmlDocument(string testCaseName)
    {
        var htmlPath = Path.Combine(TestDataPaths.Listings, testCaseName + ".htm");
        var html = File.ReadAllText(htmlPath);
        return LoadDocument(html);
    }

    public static IDocument LoadVerificationHtmlDocument(string listingId)
    {
        var htmlPath = Path.Combine(TestDataPaths.Verification, listingId + ".htm");
        var html = File.ReadAllText(htmlPath);
        return LoadDocument(html);
    }

    public static IDocument LoadDocument(string html)
    {
        // AngleSharp's HtmlParser.ParseDocument is fully synchronous
        return new HtmlParser().ParseDocument(html);
    }

    public static IDocument BuildProductPageWithCanonical(string canonicalUrl)
    {
        var html = $@"<!DOCTYPE html>
<html>
<head>
    <link rel=""canonical"" href=""{canonicalUrl}"" />
</head>
<body>
</body>
</html>";
        return new HtmlParser().ParseDocument(html);
    }
}
