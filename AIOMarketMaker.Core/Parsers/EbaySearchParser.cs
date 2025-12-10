// Services/EbayItemParser.cs
using System.Globalization;
using System.Text.RegularExpressions;
using System.Web;
using AIOMarketMaker.Core.Utils;
using AIOMarketMaker.Models.Ebay;
using AngleSharp.Dom;

namespace AIOMarketMaker.Core.Parsers
{
    public interface ISearchParser
    {
        IEnumerable<IEbayProductSummary> ParseSearchResults(IDocument document);
    }

    public sealed class EbaySearchParser : ISearchParser
    {
        public IEnumerable<IEbayProductSummary> ParseSearchResults(IDocument doc)
        {
            // Try new eBay structure first (2024+): li.s-card[data-viewport]
            var items = doc.QuerySelectorAll("li.s-card[data-viewport]");

            // Fall back to old structure if new one not found
            if (items.Length == 0)
            {
                items = doc.QuerySelectorAll("li.s-item[id]:not([id=\"\"])");
            }

            foreach (var li in items)
            {
                var id = GetListingId(li);
                if (string.IsNullOrWhiteSpace(id)) continue;
                if (IsMultiPriceListing(li)) continue;

                var isSold = IsSoldListing(li);
                yield return new EbayProductSummary(
                       ListingId: id,
                       Title: ExtractTitle(li),
                       Price: ExtractPrice(li),
                       Currency: ExtractCurrency(li),
                       ShippingCost: ExtractShippingCost(li),
                       Images: new List<string> { ExtractImageUrl(li) },
                       Url: GetListingUrl(li),
                       EndDateUtc: isSold ? ExtractDate(li) : null,
                       BuyingFormat: ExtractBuyingFormat(li),
                       Condition: ExtractCondition(li)!
                   );
            }
        }

        public string GetListingUrl(IElement li)
        {
            // New structure: a.s-card__link or any link with /itm/
            var link = li.QuerySelector("a.s-card__link")?.GetAttribute("href")
                    ?? li.QuerySelector("a[href*='/itm/']")?.GetAttribute("href")
                    ?? li.QuerySelector(".s-item__link")?.GetAttribute("href");

            return link?.Split("?").First();
        }

        public string? GetListingId(IElement li)
        {
            var url = GetListingUrl(li);
            if (string.IsNullOrEmpty(url) || !url.Contains("/itm/"))
                return null;

            var id = url.Split("/itm/")[1].Split("?").First().Trim();
            return id;
        }

        private bool IsMultiPriceListing(IElement li)
        {
            // New structure: .s-card__price, Old structure: .s-item__price
            var priceEl = li.QuerySelector(".s-card__price")
                       ?? li.QuerySelector(".s-item__price");
            return priceEl?.TextContent?.Contains("to") ?? false;
        }

        private bool IsSoldListing(IElement li)
        {
            // Check for sold indicators in various places
            var soldTag = li.QuerySelector(".s-item__title--tagblock, .POSITIVE, [class*='sold']");
            if (soldTag != null && soldTag.TextContent.Contains("Sold", StringComparison.OrdinalIgnoreCase))
                return true;

            // Also check the entire item text for "Sold" date pattern
            var fullText = li.TextContent;
            return fullText.Contains("Sold", StringComparison.OrdinalIgnoreCase);
        }

        public string ExtractTitle(IElement li)
        {
            // New structure: .s-card__title, Old structure: .s-item__title [role="heading"]
            var title = li.QuerySelector(".s-card__title")?.TextContent
                     ?? li.QuerySelector(".s-item__title [role=\"heading\"]")?.TextContent;

            // Clean up common suffixes like "Opens in a new window or tab"
            title = title?.Replace("Opens in a new window or tab", "")
                         .Replace("New listing", "")
                         .Trim();

            return title;
        }

        private string ExtractImageUrl(IElement li)
        {
            // New structure: img with ebayimg in src, Old structure: .s-item__image-wrapper img
            return li.QuerySelector("img[src*='ebayimg']")?.GetAttribute("src")
                ?? li.QuerySelector(".s-item__image-wrapper img")?.GetAttribute("src");
        }

        public BuyingFormat ExtractBuyingFormat(IElement li)
        {
            // Check for auction indicators
            if (li.QuerySelector(".s-item__bids, [class*='bid']") != null)
                return BuyingFormat.AUCTION;

            // Check for Buy It Now indicators
            if (li.QuerySelector(".s-item__dynamic.s-item__formatBestOfferEnabled") != null)
                return BuyingFormat.BUY_NOW;

            if (li.QuerySelector(".s-item__dynamic.s-item__formatBuyItNow") != null)
                return BuyingFormat.BUY_NOW;

            // Default to BUY_NOW for new structure (most sold listings are Buy It Now)
            return BuyingFormat.BUY_NOW;
        }

        public static readonly Dictionary<string, Condition> ConditionMap =
            new Dictionary<string, Condition>(StringComparer.OrdinalIgnoreCase)
        {
            { "Brand new",               Condition.NEW },
            { "Pre-owned",               Condition.USED },
            { "Opened – never used",     Condition.OPENED_NEVER_USED },
            { "Parts only",              Condition.FOR_PARTS_NOT_WORKING },
            { "Excellent - Refurbished", Condition.EXCELLENT_REFURBISHED },
            { "Very Good - Refurbished", Condition.VERY_GOOD_REFURBISHED },
            { "Good - Refurbished",      Condition.GOOD_REFURBISHED },
        };

        public static Condition ExtractCondition(IElement listItemElement)
        {
            // New structure: .s-card__subtitle-row, Old structure: .s-item__subtitle
            var subtitleTexts = listItemElement
                .QuerySelectorAll(".s-card__subtitle-row, .s-item__subtitle")
                .Select(node => node.TextContent.Trim())
                .Where(text => !string.IsNullOrEmpty(text))
                .ToList();

            // If no subtitle elements found, check the entire item text
            if (!subtitleTexts.Any())
            {
                subtitleTexts = new List<string> { listItemElement.TextContent };
            }

            var matchedPair = ConditionMap
                .FirstOrDefault(mapping =>
                    subtitleTexts.Any(text =>
                        text.IndexOf(mapping.Key, StringComparison.OrdinalIgnoreCase) >= 0
                    )
                );

            return matchedPair.Key != null
                ? matchedPair.Value
                : Condition.NULL;
        }

        public static decimal ExtractShippingCost(IElement li)
        {
            // 1) Find the first <span> whose text mentions "delivery"
            var deliverySpan = li
                .QuerySelectorAll("span")
                .FirstOrDefault(s =>
                    s.TextContent
                     .IndexOf("delivery", StringComparison.OrdinalIgnoreCase) >= 0
                );

            var rawText = deliverySpan?.TextContent?.Trim() ?? "";

            // 2) Use a regex to pull out the first number (with optional decimals/commas)
            var m = Regex.Match(rawText, @"[\d]+(?:[.,]\d{1,2})?");
            if (!m.Success)
                return 0m;

            // 3) Normalize comma vs dot, parse with invariant culture
            var normalized = m.Value.Replace(',', '.');
            return decimal.Parse(normalized, NumberStyles.Number, CultureInfo.InvariantCulture);
        }

        public static decimal ExtractPrice(IElement li)
        {
            // New structure: .s-card__price, Old structure: .s-item__price
            var rawText =
                li.QuerySelector(".s-card__price")?.TextContent
                ?? li.QuerySelector(".s-item__price .POSITIVE")?.TextContent
                ?? li.QuerySelector(".s-item__price")?.TextContent;

            if (string.IsNullOrWhiteSpace(rawText))
                return 0m;

            var cleaned = new string(rawText
                .Where(c => char.IsDigit(c) || c == '.' || c == ',')
                .ToArray());

            if (string.IsNullOrEmpty(cleaned))
                return 0m;

            if (cleaned.Count(c => c == ',') == 1 && !cleaned.Contains('.'))
                cleaned = cleaned.Replace(',', '.');

            cleaned = cleaned.Replace(",", "");

            return decimal.TryParse(cleaned, NumberStyles.Number, CultureInfo.InvariantCulture, out var price)
                ? price
                : 0m;
        }

        public static string ExtractCurrency(IElement li)
        {
            // New structure: .s-card__price, Old structure: .s-item__price
            var rawText =
                li.QuerySelector(".s-card__price")?.TextContent
                ?? li.QuerySelector(".s-item__price .POSITIVE")?.TextContent
                ?? li.QuerySelector(".s-item__price")?.TextContent;

            if (string.IsNullOrWhiteSpace(rawText))
                return null;

            var decoded = HttpUtility.HtmlDecode(rawText).Trim();

            // Pull off everything up to the first digit
            //    e.g. "£12.50" → "£"
            //         "US $15.99" → "US $"
            var symbolPart = new string(decoded
                .TakeWhile(c => !char.IsDigit(c) && c != '.' && c != ',')
                .ToArray())
                .Trim();

            return StringParsing.ToIso(symbolPart);
        }

        public DateTime? ExtractDate(IElement li)
        {
            // Search entire item text for "Sold" followed by a date
            // Format is like "Sold  25 Nov 2025" in the new eBay structure
            var fullText = li.TextContent ?? "";

            // Use regex to find "Sold" followed by a date pattern
            // Pattern: Sold followed by optional whitespace, then day month year
            var soldDateMatch = Regex.Match(fullText, @"Sold\s+(\d{1,2}\s+\w{3}\s+\d{4})", RegexOptions.IgnoreCase);
            if (soldDateMatch.Success)
            {
                var dateStr = soldDateMatch.Groups[1].Value.Trim();

                var styles = DateTimeStyles.AssumeLocal | DateTimeStyles.AdjustToUniversal;
                if (DateTime.TryParse(dateStr,
                                      CultureInfo.GetCultureInfo("en-GB"),
                                      styles,
                                      out var utcDt))
                {
                    return utcDt;
                }
            }

            // Fallback: try old structure with .POSITIVE selector
            var txt = li.QuerySelector(".POSITIVE")?.TextContent
                   ?? li.QuerySelector("[class*='sold']")?.TextContent;

            if (!string.IsNullOrWhiteSpace(txt))
            {
                txt = txt.Replace("Sold", "", StringComparison.OrdinalIgnoreCase).Trim();

                var styles = DateTimeStyles.AssumeLocal | DateTimeStyles.AdjustToUniversal;
                if (DateTime.TryParse(txt,
                                      CultureInfo.GetCultureInfo("en-GB"),
                                      styles,
                                      out var utcDt))
                {
                    return utcDt;
                }
            }

            return null;
        }

    }
}