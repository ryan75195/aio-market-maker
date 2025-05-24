using AIOMarketMaker.Etl;
using AIOMarketMaker.Etl.Utils;
using AIOMarketMaker.Services;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json;

/*
 Goal for this app is to run on a cron job and scrape a list of search terms on ebay. It will then filter for new items and then scrape the listings
 and load them into a clean product database. It should also scrape for updates on existing listings to see if they have sold yet and update accordingly. 
 It will read the search terms from an azure table.
 */
public class Program
{
    public static async Task Main(string[] args)
    {
        // 1) Build the host **without** AddHostedService
        var host = HostHelper.CreateHost(args);
        // 2) Start it (so config, DI, logging, etc. are ready)
        await host.StartAsync();

        var ebayScraper = host.Services.GetRequiredService<IEbayScraper>();

        //// active ps5 for the last 30 days
        //var date = new SearchDateRange();
        //var filter = new SearchFilter();
        //var results = await ebayScraper.SearchListings("Playstation 5 Console", null);

        //var items = await ebayScraper.GetItemsFromListings(results.Select(x => x.ListingId).ToArray());
        //await LocalStorage.WriteProductsToCsvAsync(items, "./products.csv");

        //Console.WriteLine(JsonConvert.SerializeObject(items));
        //// 4) Tear down and exit
        //await host.StopAsync();
    }
}
