// Services/EbayItemParser.cs
using System.Runtime.CompilerServices;
using System.Globalization;
using System.Web;
using AIOMarketMaker.Models.Ebay;
using AngleSharp.Dom;
using AngleSharp.Text;
using Microsoft.Extensions.Logging;
using AIOMarketMaker.Core.Utils;

[assembly: InternalsVisibleTo("AIOMarketMaker.Tests")]

namespace AIOMarketMaker.Core.Parsers
{

    public record ExtractedEbayListing(
        string? id,
        string? title,
        decimal? price,
        string? currency,
        decimal? shippingCost,
        Condition? Condition,
        IEnumerable<string>? images,
        EbayListingStatus? listingStatus,
        PurchaseFormat? purchaseFormat,
        string? ItemSpecifics,
        string? descriptionSource,
        DateTime? SoldDateUtc, // Null means it's active
        string? Location,
        string? Url
    );

    public interface IListingParser
    {
        ExtractedEbayListing ParseProductListing(IDocument document, string url);
        string? ParseDescription(IDocument document);
    }

    public class EbayListingParser : IListingParser
    {
        public ExtractedEbayListing ParseProductListing(IDocument document, string url)
        {
            var id = GetProductId(document);
            var title = GetProductTitle(document);
            var price = GetProductPrice(document);
            var currency = GetCurrency(document);
            var shippingCost = GetShippingPrice(document);
            var condition = GetProductCondition(document);
            var images = GetProductImages(document);
            var itemSpecifics = GetItemSpecifics(document);
            var description = GetItemDescriptionUrl(document);
            var listingStatus = GetListingStatus(document);
            var purchaseFormat = GetPurchaseFormat(document);
            var soldDate = GetEndDate(document);
            var location = GetLocation(document);

            return new ExtractedEbayListing(
                id: id,
                title: title,
                price: price,
                currency: currency,
                shippingCost: shippingCost,
                Condition: condition,
                images: images,
                ItemSpecifics: itemSpecifics,
                descriptionSource: description,
                listingStatus: listingStatus,
                purchaseFormat: purchaseFormat,
                SoldDateUtc: soldDate,
                Location: location,
                Url: url
            );
        }

        internal PurchaseFormat GetPurchaseFormat(IDocument document)
        {
            var buyingComponentText = document.QuerySelector(".x-buybox-cta")?.TextContent;
            
            if (buyingComponentText == null)
                return PurchaseFormat.Unknown;

            if (buyingComponentText.Contains("Submit bid") && buyingComponentText.Contains("Make offer")) 
            {
                return PurchaseFormat.AuctionWithBestOffer;
            }
            if (buyingComponentText.Contains("Buy it now") && buyingComponentText.Contains("Make offer")) 
            {
                return PurchaseFormat.BuyItNowWithBestOffer;
            }
            if (buyingComponentText.Contains("Submit bid")) 
            {
                return PurchaseFormat.Auction;
            }
            if (buyingComponentText.Contains("Buy it now")) 
            {
                return PurchaseFormat.BuyItNow;
            }

            return PurchaseFormat.Unknown;
        }

        internal EbayListingStatus? GetListingStatus(IDocument document)
        {
            var node = document.QuerySelector(".d-statusmessage")?.TextContent;
            if (node == null)
            {
                // No status message found - check if page has valid structure
                // A valid eBay listing page should have a title
                var hasTitle = document.QuerySelector(".x-item-title__mainTitle") != null;
                if (!hasTitle)
                {
                    // Page doesn't have expected structure (ended/unavailable/invalid)
                    return EbayListingStatus.Unknown;
                }
                return EbayListingStatus.Active;
            }
            else if (node.Contains("Bidding ended on "))
            {
                return EbayListingStatus.Ended;
            }
            else if (node.Contains("Item sold on") || node.Contains("This listing sold on"))
            {
                return EbayListingStatus.Sold;
            }

            return EbayListingStatus.Active;
        }

        public string? ParseDescription(IDocument document)
        {
            var node = document.QuerySelector(".x-item-description-child");
            if (node == null) {
                return null;
            }

            var text = node?.TextContent ?? string.Empty;

            text = text.Replace('\u00A0', ' ');

            text = System.Text.RegularExpressions.Regex
                .Replace(text, @"\s+", " ")
                .Trim();

            return text;
        }

        internal string? GetItemDescriptionUrl(IDocument document)
        {
            var descriptionUrl = document.QuerySelector("#desc_ifr")?.GetAttribute("src");
            return descriptionUrl;
        }

        internal string? GetItemSpecifics(IDocument document)
        {
            return document.QuerySelector(".ux-layout-section-evo.ux-layout-section--features")?.TextContent;
        }

        internal IEnumerable<string> GetProductImages(IDocument document)
        {
            var container = document.QuerySelector(".ux-image-grid.no-scrollbar");

            if (container == null)
                return new List<string>(); // return empty list if not found

            // Select all <img> tags inside that container
            var images = container
                .QuerySelectorAll("img")
                .Select(img => img.GetAttribute("src"))
                .Where(src => !string.IsNullOrWhiteSpace(src))
                .ToList();

            return images;
        }


        internal static readonly Dictionary<string, Condition> ConditionMap =
            new Dictionary<string, Condition>(StringComparer.OrdinalIgnoreCase)
        {
            { "Brand new",               Condition.NEW },
            { "New",               Condition.NEW },
            { "Pre-owned",               Condition.USED },
            { "Used",                    Condition.USED },
            { "Opened – never used",     Condition.OPENED_NEVER_USED },
            { "Parts only",              Condition.FOR_PARTS_NOT_WORKING },
            { "Excellent - Refurbished", Condition.EXCELLENT_REFURBISHED },
            { "Very Good - Refurbished", Condition.VERY_GOOD_REFURBISHED },
            { "Good - Refurbished",      Condition.GOOD_REFURBISHED },
        };

        internal Condition GetProductCondition(IDocument document)
        {
            // grab the text (null-safe)
            var el = document.QuerySelector(".x-item-condition-text .ux-textspans");
            var text = el?.Text() ?? string.Empty;

            // find the first mapping whose key appears in that text (case-insensitive)
            var matchedPair = ConditionMap
                .FirstOrDefault(mapping =>
                    // either of these will work:
                    // text.Contains(mapping.Key, StringComparison.OrdinalIgnoreCase)
                    text.IndexOf(mapping.Key, StringComparison.OrdinalIgnoreCase) >= 0
                );

            return matchedPair.Key != null
                ? matchedPair.Value
                : Condition.NULL;
        }

        internal decimal GetShippingPrice(IDocument document)
        {
            try
            {
                return decimal.Parse(document.QuerySelector(".ux-labels-values--shipping > div:nth-child(2) > div:nth-child(1) > div:nth-child(1) > span:nth-child(1)")?.TextContent.Substring(1));
            }
            catch
            {
                return 0;
            }
        }

        internal string GetCurrency(IDocument document)
        {
            var symbol = document.QuerySelector(".x-price-primary")?.TextContent.First().ToString();
            return StringParsing.ToIso(symbol);
        }

        internal decimal? GetProductPrice(IDocument document)
        {
            decimal result;
            var success = decimal.TryParse(document.QuerySelector(".x-price-primary")?.TextContent.Substring(1), out result);
            return success ? result : null;
        }

        internal string GetProductTitle(IDocument document)
        {
            return document.QuerySelector(".x-item-title__mainTitle")?.TextContent;
        }

        internal string GetProductId(IDocument document)
        {
            return document.QuerySelector(".ux-layout-section__textual-display--itemId")?.TextContent.Split(":")[1].StripLeadingTrailingSpaces();
        }

        internal DateTime? GetEndDate(IDocument document)
        {
            var text = document.QuerySelector(".d-statusmessage")?.TextContent;
            return StringParsing.ParseEndDate(text);
        }

        internal string? GetLocation(IDocument document)
        {
            var locationStr = document.QuerySelector(".ux-labels-values--shipping .ux-textspans--SECONDARY")?.TextContent;
            if (locationStr == null)
                return null;

            if (locationStr.Contains(":"))
            {
                return locationStr.Split(":")[1].Trim();
            }

            return locationStr;
        }
    }
}