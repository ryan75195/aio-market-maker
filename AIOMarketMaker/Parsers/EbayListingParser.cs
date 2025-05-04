// Services/EbayItemParser.cs
using System.Runtime.CompilerServices;
using System.Globalization;
using System.Web;
using AIOMarketMaker.Models.Ebay;
using AngleSharp.Dom;
using AngleSharp.Text;

[assembly: InternalsVisibleTo("AIOMarketMaker.Tests")]

namespace AIOMarketMaker.Services
{

    public record ExtractedEbayListing(
        string id,
        string title,
        decimal price,
        string currency,
        decimal shippingCost,
        Condition Condition,
        IEnumerable<string> images,
        EbayListingStatus listingStatus,
        PurchaseFormat purchaseFormat,
        string? ItemSpecifics,
        string? descriptionSource,
        DateTime? SoldDateUtc // Null means it's active
    );

    public interface IListingParser
    {
        ExtractedEbayListing ParseProductListing(IDocument document);
        string ParseDescription(IDocument document);
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
            var itemSpecifics = GetItemSpecifics(document);
            var description = GetItemDescriptionUrl(document);
            var listingStatus = GetListingStatus(document);
            var purchaseFormat = GetPurchaseFormat(document);

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
                SoldDateUtc: null
            );
        }

        internal PurchaseFormat GetPurchaseFormat(IDocument document)
        {
            //throw new NotImplementedException();
            return PurchaseFormat.BuyItNowWithBestOffer;
        }

        internal EbayListingStatus GetListingStatus(IDocument document)
        {
            //throw new NotImplementedException();
            return EbayListingStatus.Active;
        }

        public string ParseDescription(IDocument document)
        {
            return document.QuerySelector(".x-item-description-child").TextContent;
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
            { "Pre-owned",               Condition.USED },
            { "Opened – never used",     Condition.OPENED_NEVER_USED },
            { "Parts only",              Condition.FOR_PARTS_NOT_WORKING },
            { "Excellent - Refurbished", Condition.EXCELLENT_REFURBISHED },
            { "Very Good - Refurbished", Condition.VERY_GOOD_REFURBISHED },
            { "Good - Refurbished",      Condition.GOOD_REFURBISHED },
        };

        internal Condition GetProductCondition(IDocument document)
        {
            var subtitleTexts = document
                .QuerySelectorAll(".x-item-condition-text")
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
            return document.QuerySelector(".x-price-primary")?.TextContent.First().ToString();
        }

        internal decimal GetProductPrice(IDocument document)
        {
            return decimal.Parse(document.QuerySelector(".x-price-primary")?.TextContent.Substring(1));
        }

        internal string GetProductTitle(IDocument document)
        {
            return document.QuerySelector(".x-item-title__mainTitle")?.TextContent;
        }

        internal string GetProductId(IDocument document)
        {
            return document.QuerySelector(".ux-layout-section__textual-display--itemId")?.TextContent.Split(":")[1];
        }
    }
}