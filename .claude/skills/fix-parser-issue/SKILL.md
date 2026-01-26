---
name: fix-parser-issue
description: Fix eBay listing parser issues (status, price, title extraction). Use when parsed data doesn't match actual listing content, or when new HTML patterns aren't being recognized.
disable-model-invocation: true
allowed-tools: Read, Grep, Glob, Bash, Edit, Write
argument-hint: <listing_id> <problem_description>
---

# Fix Parser Issue

Systematically debug and fix eBay listing parser issues using evidence-based investigation.

## Usage

```
/fix-parser-issue <listing_id> <problem_description>
```

**Examples:**
- `/fix-parser-issue 397530543947 status shows Active but listing is ended`
- `/fix-parser-issue 123456789 price not extracted correctly`
- `/fix-parser-issue 987654321 title missing special characters`

## Available Tools

Python scripts in this skill's `scripts/` directory:

| Script | Purpose | Commands |
|--------|---------|----------|
| `db_query.py` | Query/manage SQL Server LocalDB | `get`, `delete`, `list` |
| `blob_download.py` | Download HTML from blob storage | `search`, `download` |
| `html_search.py` | Search HTML for patterns | `status`, `search`, `patterns` |

**Script location:** `$SKILL_DIR/scripts/`

---

## Phase 1: Gather Evidence

### 1.1 Check Database Status

```bash
python $SKILL_DIR/scripts/db_query.py get $0
```

This shows: ID, ListingId, Title, Status, Price, Currency

### 1.2 Download HTML from Blob Storage

```bash
python $SKILL_DIR/scripts/blob_download.py download $0
```

Searches Azurite (local) first, then Azure. Downloads largest HTML (main listing page) to `AIOMarketMaker.Tests/Data/Listings/Verification/`.

### 1.3 Analyze HTML Status

```bash
python $SKILL_DIR/scripts/html_search.py status AIOMarketMaker.Tests/Data/Listings/Verification/$0.htm
```

Reports: detected status, status indicators found, has title/price.

### 1.4 Search for Specific Patterns

```bash
# Search all known patterns
python $SKILL_DIR/scripts/html_search.py patterns AIOMarketMaker.Tests/Data/Listings/Verification/$0.htm

# Search custom pattern
python $SKILL_DIR/scripts/html_search.py search <file> "your regex pattern"
```

---

## Phase 2: Identify Root Cause

### 2.1 Compare Evidence

| Question | How to Check |
|----------|--------------|
| What does DB say? | `db_query.py get` output |
| What does HTML say? | `html_search.py status` output |
| Do they match? | Compare Status fields |

### 2.2 Find Parser Logic

The parser is at: `AIOMarketMaker.Core/Parsers/EbayListingParser.cs`

Key methods:
- `GetListingStatus()` - determines Active/Ended/Sold
- `GetProductPrice()` - extracts price
- `GetCurrency()` - extracts currency code
- `GetProductTitle()` - extracts title

Read the relevant method and compare against what patterns exist in the HTML.

### 2.3 Document the Gap

Write down:
1. **HTML contains:** (exact text from html_search)
2. **Parser checks for:** (patterns from source code)
3. **Gap:** (what's missing)

---

## Phase 3: Fix with TDD

### 3.1 Write Failing Test

Add to `AIOMarketMaker.Tests/UnitTests/ListingParserUnitTests.cs`:

```csharp
[Test]
public void Should_parse_<listing_id>_<expected_behavior>()
{
    var doc = PageBuilder.LoadVerificationHtmlDocument("$0");
    var parser = (EbayListingParser)_serviceUnderTest;

    // Test the specific field that's broken
    var result = parser.<MethodUnderTest>(doc);

    Assert.That(result, Is.EqualTo(<expected_value>),
        "<description of what should happen>");
}
```

### 3.2 Verify RED

```bash
cd AIOMarketMaker && dotnet test --filter "Should_parse_$0"
```

**Must fail** with expected vs actual mismatch.

### 3.3 Fix Parser

Edit `EbayListingParser.cs` to handle the new pattern.

### 3.4 Verify GREEN

```bash
cd AIOMarketMaker && dotnet test --filter "Should_parse_$0"
```

### 3.5 Run All Parser Tests

```bash
cd AIOMarketMaker && dotnet test --filter "ListingParserUnitTests"
```

Ensure no regressions.

---

## Phase 4: Clean Up Database

### 4.1 Delete Incorrect Record

```bash
python $SKILL_DIR/scripts/db_query.py delete $0
```

### 4.2 Verify via API

```bash
curl http://localhost:7071/api/listings/active | jq '.[] | select(.listingId == "$0")'
```

Should return empty.

---

## Phase 5: Commit

```bash
git add -A
git commit -m "fix(<parser_area>): <what was fixed>

<Root cause explanation>

- Added test for listing $0
- Updated <method> to handle <new pattern>
- Cleaned up incorrect database record

Co-Authored-By: Claude <noreply@anthropic.com>"
```

---

## Quick Reference

### Database Connection
- **Type:** SQL Server LocalDB (NOT SQLite)
- **Server:** `(localdb)\MSSQLLocalDB`
- **Database:** `AIOMarketMaker`

### Blob Storage
1. **Azurite (local):** Container `html`, port 10000
2. **Azure:** Account `YOUR_STORAGE_ACCOUNT`, container `scrape-results`

### Test Files
- **Location:** `AIOMarketMaker.Tests/Data/Listings/Verification/`
- **Format:** `<listing_id>.htm`

### Known Status Patterns
| HTML Pattern | Status |
|--------------|--------|
| `"Bidding ended on "` | Ended |
| `"This listing was ended"` | Ended |
| `"Item sold on"` | Sold |
| `"This listing sold on"` | Sold |
| No status + has title | Active |
