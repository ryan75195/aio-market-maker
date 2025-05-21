// Services/EbayItemParser.cs
using System.Globalization;
using System.Text.RegularExpressions;
using System.Web;
using AIOMarketMaker.Api.Utils;
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
            var items = doc.QuerySelectorAll(
                "li.s-item[id]:not([id=\"\"])"
            );

            foreach (var li in items)
            {
                var id = GetListingId(li);
                if (string.IsNullOrWhiteSpace(id)) continue;
                if (isMultiPriceListing(li)) continue;

                var soldTag = li.QuerySelector(".s-item__title--tagblock, .POSITIVE");
                var isSold = soldTag != null && soldTag.TextContent.Contains("Sold", StringComparison.OrdinalIgnoreCase);
                yield return new EbayProductSummary(
                       ListingId: id,
                       Title: ExtractTitle(li),
                       Price: ExtractPrice(li),
                       Currency: ExtractCurrency(li),
                       ShippingCost: ExtractShippingCost(li),
                       Images: new List<string> { li.QuerySelector(".s-item__image-wrapper img")?.GetAttribute("src") },
                       Url: GetListingUrl(li),
                       EndDateUtc: isSold ? ExtractDate(li) : null,
                       BuyingFormat: ExtractBuyingFormat(li),
                       Condition: ExtractCondition(li)!
                   );
            }
        }

        public string GetListingUrl(IElement li)
        {
            return li.QuerySelector(".s-item__link")?.GetAttribute("href").Split("?").First();
        }

        public string? GetListingId(IElement li)
        {
            var url = GetListingUrl(li);
            var id = url.Split("/itm/")[1].Split("?").First().Trim();
            return id;
        }

        private bool isMultiPriceListing(IElement li)
        {
            return li.QuerySelector(".s-item__price").TextContent.Contains("to");
        }

        public string ExtractTitle(IElement li)
        {
            return li.QuerySelector(".s-item__title [role=\"heading\"]")?.TextContent.Trim();
        }

        public BuyingFormat ExtractBuyingFormat(IElement li)
        {
            if (li.QuerySelector(".s-item__bids") != null)
                return BuyingFormat.AUCTION;

            if (li.QuerySelector(".s-item__dynamic.s-item__formatBestOfferEnabled") != null)
                return BuyingFormat.BUY_NOW;

            if (li.QuerySelector(".s-item__dynamic.s-item__formatBuyItNow") != null)
                return BuyingFormat.BUY_NOW;

            return BuyingFormat.NULL;
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
            var rawText =
                li.QuerySelector(".s-item__price .POSITIVE")?.TextContent
                ?? li.QuerySelector(".s-item__price")?.TextContent;

            var cleaned = new string(rawText
                .Where(c => char.IsDigit(c) || c == '.' || c == ',')
                .ToArray());

            if (cleaned.Count(c => c == ',') == 1 && !cleaned.Contains('.'))
                cleaned = cleaned.Replace(',', '.');

            cleaned = cleaned.Replace(",", "");

            return decimal.Parse(cleaned);
        }

        public static string ExtractCurrency(IElement li)
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

            return StringParsing.ToIso(symbolPart);
        }

        public DateTime? ExtractDate(IElement li)
        {
            var txt = li.QuerySelector(".POSITIVE")?.TextContent
                         ?.Replace("Sold", "", StringComparison.OrdinalIgnoreCase)
                         .Trim();
            if (string.IsNullOrWhiteSpace(txt))
                return null;

            // Tell TryParse: assume the text is local, then adjust the result to UTC.
            var styles = DateTimeStyles.AssumeLocal | DateTimeStyles.AdjustToUniversal;
            if (DateTime.TryParse(txt,
                                  CultureInfo.GetCultureInfo("en-GB"),
                                  styles,
                                  out var utcDt))
            {
                // utcDt.Kind == DateTimeKind.Utc
                return utcDt;
            }

            return null;
        }

    }
}