using AIOMarketMaker.Core.Data.Models;

namespace AIOMarketMaker.Core.Data;

public static class ScrapeJobQueryExtensions
{
    public static IQueryable<ScrapeJob> WhereEffectivelyEnabled(this IQueryable<ScrapeJob> query)
    {
        return query.Where(j => j.IsEnabled
            && (!j.JobCategories.Any() || j.JobCategories.Any(jc => jc.Category.IsEnabled)));
    }
}
