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

## Tools

| Script | Purpose | Commands |
|--------|---------|----------|
| `db_query.py` | Query/manage SQL Server LocalDB | `get`, `delete`, `list` |
| `blob_download.py` | Download HTML from blob storage | `search`, `download` |

**Script location:** `.claude/skills/fix-parser-issue/scripts/`

---

## Phase 1: Gather Evidence

### 1.1 Check Database Status

```bash
python .claude/skills/fix-parser-issue/scripts/db_query.py get $0
```

### 1.2 Download HTML

```bash
python .claude/skills/fix-parser-issue/scripts/blob_download.py download $0
```

Downloads to: `AIOMarketMaker.Tests/Data/Listings/Verification/$0.htm`

### 1.3 Analyze HTML (Use Your Judgment)

Read the downloaded HTML and search for relevant content:

1. **For status issues:** Search for status indicators
   - `grep -i "ended\|sold\|no longer available" <file>`
   - Look in `.d-statusmessage` element area

2. **For price issues:** Search for price elements
   - `grep -i "x-price-primary\|itemprop=\"price\"" <file>`

3. **For title issues:** Search for title elements
   - `grep -i "x-item-title__mainTitle" <file>`

**Use your judgment** to identify:
- What the HTML actually contains
- What pattern the parser should recognize
- Why the current parser logic fails

---

## Phase 2: Identify Root Cause

### 2.1 Read Parser Source

The parser is at: `AIOMarketMaker.Core/Parsers/EbayListingParser.cs`

Key methods:
- `GetListingStatus()` - Active/Ended/Sold
- `GetProductPrice()` - price extraction
- `GetCurrency()` - currency code
- `GetProductTitle()` - title extraction

### 2.2 Compare and Document

| What HTML Contains | What Parser Checks | Gap |
|--------------------|-------------------|-----|
| (from your analysis) | (from source code) | (missing pattern) |

---

## Phase 3: Fix with TDD

### 3.1 Write Failing Test

```csharp
[Test]
public void Should_parse_$0_<expected_behavior>()
{
    var doc = PageBuilder.LoadVerificationHtmlDocument("$0");
    var parser = (EbayListingParser)_serviceUnderTest;
    var result = parser.<Method>(doc);
    Assert.That(result, Is.EqualTo(<expected>));
}
```

### 3.2 RED → GREEN → Refactor

```bash
# Verify fails
dotnet test --filter "Should_parse_$0"

# Fix parser, then verify passes
dotnet test --filter "Should_parse_$0"

# Check no regressions
dotnet test --filter "ListingParserUnitTests"
```

---

## Phase 4: Clean Up

### 4.1 Delete Bad Record

```bash
python .claude/skills/fix-parser-issue/scripts/db_query.py delete $0
```

### 4.2 Commit

```bash
git add -A && git commit -m "fix(parser): <what was fixed>

- Added test for listing $0
- Updated <method> to handle <pattern>

Co-Authored-By: Claude <noreply@anthropic.com>"
```

---

## Reference

### Database
- **Type:** SQL Server LocalDB
- **Connection:** `(localdb)\MSSQLLocalDB` / `AIOMarketMaker`

### Blob Storage
1. Azurite (local): container `html`
2. Azure: account `YOUR_STORAGE_ACCOUNT`, container `scrape-results`

### Known Status Patterns
| Pattern | Status |
|---------|--------|
| `"Bidding ended on "` | Ended |
| `"This listing was ended"` | Ended |
| `"Item sold on"` | Sold |
| `"This listing sold on"` | Sold |
| No status + has title | Active |
