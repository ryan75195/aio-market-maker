using AIOMarketMaker.Core.Services;
using AIOMarketMaker.Core.Models.Azure;
using Microsoft.Extensions.Configuration; // Added for IConfiguration
using System;
using System.Collections.Generic;
using System.IO; // Required for Path
using System.Linq;
using System.Threading.Tasks;

// Build configuration
IConfiguration configuration = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory) // Ensures appsettings.json is found
    .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
    .AddEnvironmentVariables() // Optional: to allow overriding with environment variables
    .Build();

// Read configuration values
string connectionString = configuration.GetValue<string>("AzureTableStorage:ConnectionString");
string tableName = configuration.GetValue<string>("AzureTableStorage:SearchTermsTableName");

// Validate configuration
if (string.IsNullOrEmpty(connectionString) || connectionString == "YOUR_AZURE_STORAGE_CONNECTION_STRING")
{
    Console.WriteLine("Warning: Azure Storage Connection String is not configured or is still set to the placeholder in appsettings.json. Please update it.");
    // In a real application, you might want to exit or throw an exception here
    // For this exercise, we'll allow it to proceed but it will likely fail when interacting with Azure.
}

if (string.IsNullOrEmpty(tableName))
{
    Console.WriteLine("Warning: Azure Storage Table Name is not configured in appsettings.json.");
    // Set a default or handle as an error
    tableName = "SearchTerms"; // Fallback to a default if not specified, though it's better to require it.
}

Console.WriteLine("Starting ETL process...");

var azureTableService = new AzureTableService(connectionString, tableName);

Console.WriteLine($"Attempting to retrieve search terms from table: {tableName}...");

try
{
    IEnumerable<SearchTermEntity> searchTerms = await azureTableService.GetSearchTermsAsync();

    if (searchTerms.Any())
    {
        Console.WriteLine("Retrieved search terms:");
        foreach (var termEntity in searchTerms)
        {
            // Assuming SearchTermEntity has a 'Term' property.
            // If it's different (e.g., from ITableEntity, it might be RowKey or another property)
            // adjust accordingly. For now, we'll stick to the 'Term' property as per the previous structure.
            Console.WriteLine($"Search Term: {termEntity.Term}");
        }
    }
    else
    {
        Console.WriteLine("No search terms found in the table.");
    }
}
catch (Exception ex)
{
    Console.WriteLine($"An error occurred during the ETL process: {ex.Message}");
    // In a real application, log the full exception details, including stack trace.
    // Console.WriteLine(ex.ToString()); // For more detailed error info during development
}

Console.WriteLine("ETL process finished.");
