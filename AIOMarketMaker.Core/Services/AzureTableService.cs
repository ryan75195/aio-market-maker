using AIOMarketMaker.Core.Models.Azure;
using Azure.Data.Tables; // Added
using System; // Added
using System.Collections.Generic;
using System.Linq; // Ensured
using System.Threading.Tasks;
using Azure; // Required for RequestFailedException

namespace AIOMarketMaker.Core.Services
{
    public class AzureTableService
    {
        private readonly TableClient _tableClient;
        // _connectionString and _tableName fields are no longer needed as _tableClient holds this.

        public AzureTableService(string connectionString, string tableName)
        {
            if (string.IsNullOrEmpty(connectionString) || connectionString.Equals("YOUR_CONNECTION_STRING", StringComparison.OrdinalIgnoreCase) || connectionString.Equals("YOUR_AZURE_STORAGE_CONNECTION_STRING", StringComparison.OrdinalIgnoreCase))
            {
                // The ETL Program.cs already warns, so this service might assume valid inputs or throw.
                // For robustness, let's make it throw if critical configuration is missing/default.
                throw new ArgumentException("Connection string is missing or not configured properly.", nameof(connectionString));
            }
            if (string.IsNullOrEmpty(tableName))
            {
                throw new ArgumentException("Table name is missing.", nameof(tableName));
            }
            _tableClient = new TableClient(connectionString, tableName);
        }

        public async Task<IEnumerable<SearchTermEntity>> GetSearchTermsAsync()
        {
            var terms = new List<SearchTermEntity>();
            try
            {
                // Ensure the table exists, create it if it doesn't.
                // This is good for robustness but might be handled differently based on requirements
                // (e.g., ETL fails if table doesn't exist).
                // For now, let's add a create if not exists call.
                await _tableClient.CreateIfNotExistsAsync(); 
                
                Console.WriteLine($"Querying table '{_tableClient.Name}' for search terms...");
                
                // Query all entities in the table.
                // Note: For large tables, consider pagination and more specific queries.
                // Assuming all entities in this table are SearchTermEntity.
                // If PartitionKey was set during entity creation, a filter like "PartitionKey eq 'YourPartitionKeyValue'" could be used.
                // For this example, we fetch all.
                var queryResults = _tableClient.QueryAsync<SearchTermEntity>();
                
                await foreach (var entity in queryResults)
                {
                    terms.Add(entity);
                }
                Console.WriteLine($"Found {terms.Count} terms in table '{_tableClient.Name}'.");
            }
            catch (RequestFailedException ex)
            {
                Console.WriteLine($"Error accessing Azure Table '{_tableClient.Name}': {ex.Status} - {ex.Message}");
                // Depending on policy, you might re-throw, return empty, or handle specifically.
                // For now, we log and return the (possibly empty) list.
                // Consider logging ex.StackTrace for more details in a real scenario.
            }
            catch (Exception ex)
            {
                Console.WriteLine($"An unexpected error occurred in GetSearchTermsAsync while processing table '{_tableClient.Name}': {ex.Message}");
                // Rethrowing might be appropriate here if it's truly unexpected and unrecoverable at this level.
                // Or, log and return empty list. Consider logging ex.StackTrace.
            }
            return terms;
        }
    }
}
