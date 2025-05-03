// Services/EbayItemParser.cs
using System.Globalization;
using System.Web;
using AIOMarketMaker.Models.Ebay;
using AngleSharp.Dom;
using AngleSharp.Text;

namespace AIOMarketMaker.Services
{

    public record ExtractedEbayListing(
        string id,
        string title,
        decimal? price,
        string? currency,
        decimal? shippingCost,
        Condition? Condition,
        IEnumerable<string> images,
        string? ItemSpecifics,
        string? descriptionSource,
        string? url,
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
            var description = GetItemDescription(document);
            var url = GetProductUrl(document);

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
                url: url,
                SoldDateUtc: null
            );
        }

        public string ParseDescription(IDocument document)
        {
            return document.QuerySelector(".x-item-description-child").TextContent;
        }

        private string? GetProductUrl(IDocument document)
        {
            return document.BaseUri;
        }

        private string? GetItemDescription(IDocument document)
        {
            // come back to this one as we need to navigate to this URL and get the content
            var descriptionUrl = document.QuerySelector("#desc_ifr")?.GetAttribute("src");
            return descriptionUrl;
        }

        private string? GetItemSpecifics(IDocument document)
        {
            return document.QuerySelector(".ux-layout-section-evo.ux-layout-section--features")?.TextContent;
        }

        private IEnumerable<string> GetProductImages(IDocument document)
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

        private Condition? GetProductCondition(IDocument document)
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

        private decimal? GetShippingPrice(IDocument document)
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

        private string? GetCurrency(IDocument document)
        {
            return document.QuerySelector(".x-price-primary")?.TextContent.First().ToString();
        }

        private decimal? GetProductPrice(IDocument document)
        {
            return decimal.Parse(document.QuerySelector(".x-price-primary")?.TextContent.Substring(1));
        }

        private string GetProductTitle(IDocument document)
        {
            return document.QuerySelector(".x-item-title__mainTitle")?.TextContent;
        }

        private string GetProductId(IDocument document)
        {
            return document.QuerySelector(".ux-layout-section__textual-display--itemId")?.TextContent.Split(":")[1];
        }
    }
}