using AIOMarketMaker.Models.Ebay;
using CsvHelper.Configuration;
using CsvHelper;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Globalization;
using CsvHelper;
using CsvHelper.Configuration;
using System.IO;

namespace AIOMarketMaker.Etl.Utils
{
    static internal class LocalStorage
    {
        public static async Task WriteProductsToCsvAsync(IEnumerable<EbayProduct> products, string outputPath)
        {
            var config = new CsvConfiguration(CultureInfo.InvariantCulture)
            {
                HasHeaderRecord = true,
                TrimOptions = TrimOptions.Trim,
                NewLine = Environment.NewLine
            };

            await using var writer = new StreamWriter(outputPath);
            await using var csv = new CsvWriter(writer, config);

            // Optional: flatten ItemSpecifics if needed
            foreach (var product in products)
            {
                // Flatten the ItemSpecifics dictionary into a string if needed
                var dict = product.ItemSpecifics is not null
                    ? System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(product.ItemSpecifics)
                    : null;

                var flatItemSpecifics = dict is not null
                    ? string.Join("; ", dict.Select(kvp => $"{kvp.Key}={kvp.Value}"))
                    : "";

                // Write anonymous object to flatten nested/complex properties
                csv.WriteRecord(new
                {
                    product.ListingId,
                    product.Title,
                    product.Price,
                    product.Currency,
                    product.ShippingCost,
                    product.Url,
                    product.Condition,
                    product.ListingStatus,
                    product.PurchaseFormat,
                    Description = product.Description ?? "",
                    ItemSpecifics = flatItemSpecifics,
                    EndDateUtc = product.EndDateUtc?.ToString("o"),
                    product.Location,
                    Images = string.Join(",", product.Images)
                });

                await csv.NextRecordAsync();
            }
        }

    }
}
