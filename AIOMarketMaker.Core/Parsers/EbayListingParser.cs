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
        DateTime? SoldDateUtc, // Null means it's active
        string? Url
    );

    public interface IListingParser
    {
        ExtractedEbayListing ParseProductListing(IDocument document);
        string? ParseDescription(IDocument document);
        bool IsProductCatalogPage(IDocument document);
    }

    public class EbayListingParser : IListingParser
    {
        public ExtractedEbayListing ParseProductListing(IDocument document)
        {
            var id = GetProductId(document);
            var title = GetProductTitle(document);
            var price = GetProductPrice(document);
            var currency = GetCurrency(document);
            var shippingCost = GetShippingPrice(document);
            var condition = GetProductCondition(document);
            var images = GetProductImages(document);
            var listingStatus = GetListingStatus(document);
            var purchaseFormat = GetPurchaseFormat(document);
            var soldDate = GetEndDate(document);
            var url = id != null ? $"https://www.ebay.co.uk/itm/{id}" : null;

            return new ExtractedEbayListing(
                id: id,
                title: title,
                price: price,
                currency: currency,
                shippingCost: shippingCost,
                Condition: condition,
                images: images,
                listingStatus: listingStatus,
                purchaseFormat: purchaseFormat,
                SoldDateUtc: soldDate,
                Url: url
            );
        }

        internal PurchaseFormat GetPurchaseFormat(IDocument document)
        {
            var buyingComponentText = document.QuerySelector(".x-buybox-cta")?.TextContent;

            // Try old-style HTML where buttons are in x-buybox-cta
            if (buyingComponentText != null)
            {
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
            }

            // Fallback for new-style HTML where buttons are rendered client-side
            // Check the price section for "or Best Offer" text
            var priceText = document.QuerySelector(".x-price-primary")?.TextContent;
            if (priceText != null)
            {
                var hasBestOffer = priceText.Contains("Best Offer", StringComparison.OrdinalIgnoreCase);

                // If we have a price, it's at minimum a BuyItNow listing
                // Check for "Best Offer" to determine if offers are accepted
                if (hasBestOffer)
                {
                    return PurchaseFormat.BuyItNowWithBestOffer;
                }

                // Has price but no Best Offer text - assume BuyItNow
                return PurchaseFormat.BuyItNow;
            }

            return PurchaseFormat.Unknown;
        }

        internal EbayListingStatus? GetListingStatus(IDocument document)
        {
            // Check for out of stock warning message first
            var warningMessage = document.QuerySelector(".ux-message--INLINE-WARNING .ux-textspans")?.TextContent;
            if (warningMessage != null && warningMessage.Contains("out of stock", StringComparison.OrdinalIgnoreCase))
            {
                return EbayListingStatus.OutOfStock;
            }

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
            else if (node.Contains("Bidding ended on ") || node.Contains("This listing was ended"))
            {
                return EbayListingStatus.Ended;
            }
            else if (node.Contains("Item sold on") || node.Contains("This listing sold on") || node.Contains("This Buy It Now listing sold on"))
            {
                return EbayListingStatus.Sold;
            }

            return EbayListingStatus.Active;
        }

        public string? ParseDescription(IDocument document)
        {
            var node = document.QuerySelector(".x-item-description-child");
            if (node == null)
            {
                return null;
            }

            return DescriptionCleaner.Clean(node.InnerHtml);
        }


        internal IEnumerable<string> GetProductImages(IDocument document)
        {
            // Try ux-image-grid first (older eBay HTML structure)
            var container = document.QuerySelector(".ux-image-grid.no-scrollbar");

            // Fallback to ux-image-carousel (newer eBay HTML structure)
            if (container == null)
            {
                container = document.QuerySelector(".ux-image-carousel");
            }

            if (container == null)
                return new List<string>();

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
            // Try new structure first: get text from first span inside x-price-primary
            var priceSpan = document.QuerySelector(".x-price-primary > .ux-textspans");
            var priceText = priceSpan?.TextContent?.Trim();

            // Fall back to old structure: direct text content
            if (string.IsNullOrEmpty(priceText))
                priceText = document.QuerySelector(".x-price-primary")?.TextContent?.Trim();

            if (string.IsNullOrEmpty(priceText))
                return null;

            // Extract currency prefix (everything before first digit)
            var currencyPrefix = ExtractCurrencyPrefix(priceText);
            return StringParsing.ToIso(currencyPrefix);
        }

        internal decimal? GetProductPrice(IDocument document)
        {
            // Try new structure first: get text from first span inside x-price-primary
            var priceSpan = document.QuerySelector(".x-price-primary > .ux-textspans");
            var priceText = priceSpan?.TextContent?.Trim();

            // Fall back to old structure: direct text content
            if (string.IsNullOrEmpty(priceText))
                priceText = document.QuerySelector(".x-price-primary")?.TextContent?.Trim();

            if (string.IsNullOrEmpty(priceText))
                return null;

            // Extract numeric portion (everything from first digit onwards, stopping at non-numeric)
            var numericPart = ExtractNumericPortion(priceText);
            if (decimal.TryParse(numericPart, out var result))
                return result;

            return null;
        }

        /// <summary>
        /// Extract currency prefix from price text (e.g., "£" from "£99.99", "US $" from "US $99.99")
        /// </summary>
        private static string ExtractCurrencyPrefix(string priceText)
        {
            var firstDigitIndex = -1;
            for (int i = 0; i < priceText.Length; i++)
            {
                if (char.IsDigit(priceText[i]))
                {
                    firstDigitIndex = i;
                    break;
                }
            }

            if (firstDigitIndex <= 0)
                return priceText.Length > 0 ? priceText[0].ToString() : null;

            return priceText.Substring(0, firstDigitIndex).Trim();
        }

        /// <summary>
        /// Extract numeric portion from price text (e.g., "99.99" from "£99.99" or "US $99.99")
        /// Stops at first non-numeric character after the number starts
        /// </summary>
        private static string ExtractNumericPortion(string priceText)
        {
            var firstDigitIndex = -1;
            for (int i = 0; i < priceText.Length; i++)
            {
                if (char.IsDigit(priceText[i]))
                {
                    firstDigitIndex = i;
                    break;
                }
            }

            if (firstDigitIndex < 0)
                return null;

            // Find end of number (digits and decimal points/commas)
            var endIndex = firstDigitIndex;
            for (int i = firstDigitIndex; i < priceText.Length; i++)
            {
                if (char.IsDigit(priceText[i]) || priceText[i] == '.' || priceText[i] == ',')
                    endIndex = i + 1;
                else
                    break;
            }

            return priceText.Substring(firstDigitIndex, endIndex - firstDigitIndex);
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

        /// <summary>
        /// Detects if the page is a product catalog page (redirected from /itm/ to /p/).
        /// Product catalog pages have a different HTML structure and cannot be parsed as individual listings.
        /// </summary>
        public bool IsProductCatalogPage(IDocument document)
        {
            var canonical = document.QuerySelector("link[rel='canonical']")?.GetAttribute("href");
            return canonical != null && canonical.Contains("/p/");
        }
    }
}