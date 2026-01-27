namespace AIOMarketMaker.Core.Services;

public static class ListingStatusHelper
{
    private static readonly Dictionary<string, int> StatusRank = new(StringComparer.OrdinalIgnoreCase)
    {
        { "Active", 0 },
        { "OutOfStock", 1 },
        { "Ended", 2 },
        { "Sold", 3 }
    };

    public static int GetStatusRank(string? status)
    {
        if (string.IsNullOrEmpty(status))
            return -1;

        return StatusRank.GetValueOrDefault(status, -1);
    }

    public static bool CanUpdateStatus(string? existingStatus, string? newStatus)
    {
        var existingRank = GetStatusRank(existingStatus);
        var newRank = GetStatusRank(newStatus);

        // Unknown/null existing status can always be updated
        if (existingRank < 0)
            return true;

        // New status must be same or higher rank
        return newRank >= existingRank;
    }
}
