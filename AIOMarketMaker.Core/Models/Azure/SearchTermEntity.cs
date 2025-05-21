using System;
using Azure;
using Azure.Data.Tables;

namespace AIOMarketMaker.Core.Models.Azure
{
    public class SearchTermEntity : ITableEntity
    {
        public string Term { get; set; }
        public string PartitionKey { get; set; }
        public string RowKey { get; set; }
        public DateTimeOffset? Timestamp { get; set; }
        public ETag ETag { get; set; }
    }
}
