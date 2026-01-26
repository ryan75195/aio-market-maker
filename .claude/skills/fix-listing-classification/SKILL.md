---
name: fix-listing-classification
description: Fix incorrectly classified eBay listings (Active showing as Ended, or vice versa). Use when a listing's status in the database doesn't match its actual eBay status.
disable-model-invocation: true
allowed-tools: Read, Grep, Glob, Bash, Edit, Write
---

# Fix Listing Classification Issues

This skill guides you through systematically debugging and fixing listing classification issues where the database status doesn't match the actual eBay listing status.

## Required Arguments

Usage: `/fix-listing-classification <listing_id>`

Example: `/fix-listing-classification 397530543947`

## Phase 1: Gather Evidence

### Step 1.1: Check Database Status

Query the SQL Server LocalDB to see the current status:

```bash
# Create and run a quick C# program to check the listing
cat > /tmp/check-listing.cs << 'EOF'
using Microsoft.Data.SqlClient;
var conn = new SqlConnection(@"Server=(localdb)\MSSQLLocalDB;Database=AIOMarketMaker;Trusted_Connection=True;TrustServerCertificate=True;");
conn.Open();
var cmd = new SqlCommand("SELECT Id, ListingId, Title, ListingStatus FROM Listings WHERE ListingId = @id", conn);
cmd.Parameters.AddWithValue("@id", "$ARGUMENTS");
var reader = cmd.ExecuteReader();
if (reader.Read()) Console.WriteLine($"DB Status: {reader["ListingStatus"]}\nTitle: {reader["Title"]}");
else Console.WriteLine("Listing not found in database");
EOF
```

### Step 1.2: Get HTML from Blob Storage

Check Azurite (local) first, then Azure blob storage:

**Azurite (local development):**
```powershell
$connectionString = "DefaultEndpointsProtocol=http;AccountName=devstoreaccount1;AccountKey=Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==;BlobEndpoint=http://127.0.0.1:10000/devstoreaccount1;"
az storage blob list --container-name html --connection-string $connectionString --query "[?contains(name, '$ARGUMENTS')]" -o table
```

**Azure blob storage:**
```bash
az storage blob list --account-name YOUR_STORAGE_ACCOUNT --container-name scrape-results --query "[?contains(name, '$ARGUMENTS')]" -o table
```

### Step 1.3: Download and Analyze HTML

Download the largest HTML blob (main listing page, typically 1-2MB):

```powershell
# Download to verification folder
$outPath = "AIOMarketMaker.Tests/Data/Listings/Verification/$ARGUMENTS.htm"
az storage blob download --container-name html --name <blob_name> --file $outPath --connection-string $connectionString
```

### Step 1.4: Search for Status Indicators

Look for these patterns in the HTML:

```bash
# Search for ended/sold indicators
grep -oi "this listing.*ended\|bidding.*ended\|item sold on\|listing sold on\|no longer available" <html_file>
```

**Known eBay status patterns:**
| Pattern in HTML | Correct Status |
|-----------------|----------------|
| `"Bidding ended on "` | Ended |
| `"This listing was ended"` | Ended |
| `"Item sold on"` | Sold |
| `"This listing sold on"` | Sold |
| No status message + has title | Active |

## Phase 2: Identify Root Cause

### Step 2.1: Check Parser Logic

Read the `GetListingStatus` method in `EbayListingParser.cs`:

```csharp
// Located at: AIOMarketMaker.Core/Parsers/EbayListingParser.cs
// Method: GetListingStatus(IDocument document)
```

Compare the patterns in the HTML against what the parser checks for.

### Step 2.2: Document the Gap

If the HTML contains a status pattern the parser doesn't recognize:
- Note the exact text from the HTML
- Note what patterns the parser currently checks
- This is your root cause

## Phase 3: Fix the Parser (TDD)

### Step 3.1: Write Failing Test

Add a test to `ListingParserUnitTests.cs`:

```csharp
[Test]
public void Should_parse_<listing_id>_as_<expected_status>()
{
    var doc = PageBuilder.LoadVerificationHtmlDocument("$ARGUMENTS");
    var parser = (EbayListingParser)_serviceUnderTest;
    var status = parser.GetListingStatus(doc);

    Assert.That(status, Is.EqualTo(EbayListingStatus.<ExpectedStatus>),
        "Status should be <ExpectedStatus>");
}
```

### Step 3.2: Run Test (Expect RED)

```bash
dotnet test --filter "FullyQualifiedName~Should_parse_$ARGUMENTS"
```

Verify it fails with: `Expected: <ExpectedStatus>, But was: Active`

### Step 3.3: Fix Parser

Add the missing pattern to `GetListingStatus()`:

```csharp
else if (node.Contains("existing pattern") || node.Contains("NEW PATTERN"))
{
    return EbayListingStatus.<CorrectStatus>;
}
```

### Step 3.4: Run Test (Expect GREEN)

```bash
dotnet test --filter "FullyQualifiedName~Should_parse_$ARGUMENTS"
```

### Step 3.5: Run All Parser Tests

```bash
dotnet test --filter "FullyQualifiedName~ListingParserUnitTests"
```

Ensure no regressions.

## Phase 4: Clean Up Database

### Step 4.1: Delete Incorrect Listing

The database is SQL Server LocalDB (NOT SQLite):

```csharp
using Microsoft.Data.SqlClient;

var connectionString = @"Server=(localdb)\MSSQLLocalDB;Database=AIOMarketMaker;Trusted_Connection=True;TrustServerCertificate=True;";
using var conn = new SqlConnection(connectionString);
conn.Open();

using var cmd = new SqlCommand("DELETE FROM Listings WHERE ListingId = @id", conn);
cmd.Parameters.AddWithValue("@id", "$ARGUMENTS");
var rows = cmd.ExecuteNonQuery();
Console.WriteLine($"Deleted {rows} row(s)");
```

### Step 4.2: Verify Deletion

Query the API to confirm listing is removed:

```powershell
$listings = Invoke-RestMethod -Uri "http://localhost:7071/api/listings/active"
$listings | Where-Object { $_.listingId -eq "$ARGUMENTS" }
```

## Phase 5: Commit Changes

```bash
git add -A
git commit -m "fix: detect <pattern> listings as <Status> status

Parser was returning Active for listings with <pattern> because
it only checked for <old patterns>.

- Added check for '<new pattern>' in GetListingStatus()
- Added verification test for listing $ARGUMENTS
- Deleted incorrectly classified listing from database

Co-Authored-By: Claude <noreply@anthropic.com>"
```

## Key Reminders

1. **Database Location:** SQL Server LocalDB, NOT SQLite (`etl.db` is unused)
2. **Connection String:** `Server=(localdb)\MSSQLLocalDB;Database=AIOMarketMaker;...`
3. **Blob Storage:** Check Azurite first (local), then Azure
4. **Test Files:** Save HTML to `AIOMarketMaker.Tests/Data/Listings/Verification/`
5. **TDD:** Always write failing test BEFORE fixing parser
