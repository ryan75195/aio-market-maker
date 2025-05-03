// Services/EbayItemParser.cs
using System.Globalization;
using System.Web;
using AIOMarketMaker.Models.Ebay;
using AngleSharp.Dom;

namespace AIOMarketMaker.Api.Parsers
{
    public interface ISearchParser
    {
        IEnumerable<IEbayProductSummary> ParseSearchResults(IDocument document);
    }

    public sealed class EbaySearchParser : ISearchParser
    {
        public IEnumerable<IEbayProductSummary> ParseSearchResults(IDocument doc)
        {
            var items = doc.QuerySelectorAll("li.s-item").Skip(2);

            foreach (var li in items)
            {
                var id = li.GetAttribute("id");
                if (string.IsNullOrWhiteSpace(id)) continue;

                var soldTag = li.QuerySelector(".s-item__title--tagblock, .POSITIVE");
                var isSold = soldTag != null && soldTag.TextContent.Contains("Sold", StringComparison.OrdinalIgnoreCase);
                yield return new EbayProductSummary(
                       id: id,
                       title: li.QuerySelector(".s-item__link")?.TextContent.Trim(),
                       price: ExtractPrice(li),
                       currency: ExtractCurrency(li),
                       shippingCost: ExtractShippingCost(li),
                       images: new List<string> { li.QuerySelector(".s-item__image-wrapper img")?.GetAttribute("src") },
                       url: li.QuerySelector(".s-item__link")?.GetAttribute("href"),
                       soldDateUtc: isSold ? ExtractDate(li) : null,
                       buyingFormat: ExtractBuyingFormat(li),
                       condition: ExtractCondition(li)!
                   );
            }
        }

        private BuyingFormat ExtractBuyingFormat(IElement li)
        {
            if (li.QuerySelector(".s-item__bids") != null)
                return BuyingFormat.AUCTION;

            if (li.QuerySelector(".s-item__dynamic.s-item__formatBestOfferEnabled") != null)
                return BuyingFormat.BUY_NOW;

            if (li.QuerySelector(".s-item__dynamic.s-item__formatBuyItNow") != null)
                return BuyingFormat.BUY_NOW;

            return BuyingFormat.NULL;
        }

        private static readonly Dictionary<string, Condition> ConditionMap =
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
            var subtitleTexts = listItemElement
                .QuerySelectorAll(".s-item__subtitle")
                .Select(node => node.TextContent.Trim())
                .Where(text => !string.IsNullOrEmpty(text));

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

        public static decimal? ExtractShippingCost(IElement li)
        {
            var rawText = li.QuerySelector("span.s-item__shipping")?.TextContent;
            if (string.IsNullOrWhiteSpace(rawText))
                return null;

            var cleaned = rawText
                .Replace("delivery", "", StringComparison.OrdinalIgnoreCase)
                .Replace("postage", "", StringComparison.OrdinalIgnoreCase)
                .Replace("Free", "0", StringComparison.OrdinalIgnoreCase)
                .Replace("+", "")
                .Trim();

            var numericPart = new string(cleaned
                .Where(c => char.IsDigit(c) || c == '.' || c == ',')
                .ToArray());

            if (numericPart.Count(c => c == ',') == 1 && !numericPart.Contains('.'))
                numericPart = numericPart.Replace(',', '.');

            numericPart = numericPart.Replace(",", "");

            return decimal.TryParse(numericPart, out var shipping)
                ? shipping
                : null;
        }

        public static decimal? ExtractPrice(IElement li)
        {
            var rawText =
                li.QuerySelector(".s-item__price .POSITIVE")?.TextContent
                ?? li.QuerySelector(".s-item__price")?.TextContent;
            if (string.IsNullOrWhiteSpace(rawText))
                return null;

            var cleaned = new string(rawText
                .Where(c => char.IsDigit(c) || c == '.' || c == ',')
                .ToArray());

            if (cleaned.Count(c => c == ',') == 1 && !cleaned.Contains('.'))
                cleaned = cleaned.Replace(',', '.');

            cleaned = cleaned.Replace(",", "");

            return decimal.TryParse(cleaned, out var price)
                ? price
                : null;
        }

        private static string? ExtractCurrency(IElement li)
        {
            var rawText =
                li.QuerySelector(".s-item__price .POSITIVE")?.TextContent
                ?? li.QuerySelector(".s-item__price")?.TextContent;

            if (string.IsNullOrWhiteSpace(rawText))
                return null;

            var decoded = HttpUtility.HtmlDecode(rawText).Trim();

            // 3) Pull off everything up to the first digit
            //    e.g. "£12.50" → "£"
            //         "US $15.99" → "US $"
            var symbolPart = new string(decoded
                .TakeWhile(c => !char.IsDigit(c) && c != '.' && c != ',')
                .ToArray())
                .Trim();

            if (string.IsNullOrEmpty(symbolPart))
                return null;

            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["£"] = "GBP",
                ["GBP"] = "GBP",
                ["$"] = "USD",
                ["US $"] = "USD",
                ["USD"] = "USD",
                ["€"] = "EUR",
                ["EUR"] = "EUR",
                ["C $"] = "CAD",
                ["CAD"] = "CAD"
            };

            if (map.TryGetValue(symbolPart, out var iso))
                return iso;

            return symbolPart;
        }

        public static DateTime? ExtractDate(IElement li)
        {
            var txt = li.QuerySelector(".POSITIVE")?.TextContent
                         ?.Replace("Sold", "", StringComparison.OrdinalIgnoreCase)
                         .Trim();
            if (string.IsNullOrWhiteSpace(txt))
                return null;

            return DateTime.TryParse(txt,
                                     CultureInfo.GetCultureInfo("en-GB"),
                                     DateTimeStyles.AssumeLocal,
                                     out var dt)
                   ? dt
                   : null;
        }
    }
}