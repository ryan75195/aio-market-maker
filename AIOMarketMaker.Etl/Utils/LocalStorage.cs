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

            // Build a list of flat records first
            var records = products.Select(p => new
            {
                p.ListingId,
                p.Title,
                p.Price,
                p.Currency,
                p.ShippingCost,
                p.Url,
                p.Condition,
                p.ListingStatus,
                p.PurchaseFormat,
                Description = p.Description ?? "",
                EndDateUtc = p.EndDateUtc?.ToString("o"),
                Images = string.Join(",", p.Images)
            }).ToList();

            if (records.Count > 0)
            {
                // 1) Write header based on the anonymous‐type’s properties
                csv.WriteHeader(records[0].GetType());
                await csv.NextRecordAsync();

                // 2) Write every record
                foreach (var rec in records)
                {
                    csv.WriteRecord(rec);
                    await csv.NextRecordAsync();
                }
            }
        }
    }
}
