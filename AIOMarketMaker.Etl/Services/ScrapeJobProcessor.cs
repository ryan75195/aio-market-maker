using AIOMarketMaker.Etl.Models;

namespace AIOMarketMaker.Etl.Services;

public interface IScrapeJobProcessor
{
    Task Process(ScrapeJobMessage message);
}

public class ScrapeJobProcessor : IScrapeJobProcessor
{
    public Task Process(ScrapeJobMessage message)
    {
        throw new NotImplementedException();
    }
}
