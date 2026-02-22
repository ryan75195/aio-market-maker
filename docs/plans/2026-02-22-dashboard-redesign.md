# Dashboard Redesign Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Redesign the overview dashboard from a 2x3 grid to a 3-2 layout, replacing two useless charts, adding an opportunities trend line, and fixing readability on three existing charts.

**Architecture:** Frontend-only changes for chart fixes/removals, plus one new API field (`OpportunitiesByDay`) for the trend chart. Chart.js v4 renders all charts.

**Tech Stack:** Chart.js v4, vanilla JS (Vue 3 petite-vue), ASP.NET 8 API, raw SQL via EF Core

---

### Task 1: Add OpportunitiesByDay to API

**Files:**
- Modify: `AIOMarketMaker.Api/Endpoints/OverviewEndpoints.cs`

**Step 1: Add the new record and response field**

Add record after line 25 (`CumulativeGrowthEntry`):
```csharp
public record OpportunitiesByDayEntry(string Date, int Count);
```

Add field to `OverviewResponse` record (after `CumulativeGrowth`):
```csharp
IEnumerable<OpportunitiesByDayEntry> OpportunitiesByDay,
```

**Step 2: Add the query method**

Add after `GetCumulativeGrowth` method (~line 174):
```csharp
private static async Task<IEnumerable<OpportunitiesByDayEntry>> GetOpportunitiesByDay(
    EtlDbContext db, PredictionFilters filters)
{
    var conn = db.Database.GetDbConnection();
    if (conn.State != ConnectionState.Open)
    {
        await conn.OpenAsync();
    }

    var isSqlite = conn.GetType().Name.Contains("Sqlite");
    var dateCast = isSqlite ? "DATE(l.CreatedUtc)" : "CAST(l.CreatedUtc AS DATE)";
    var minComps = filters.MinComps > 0 ? filters.MinComps : 1;

    var sql = $@"
        SELECT {dateCast} AS OpDate, COUNT(*) AS OpCount
        FROM Listings l
        WHERE l.ListingStatus = 'Active'
          AND (SELECT COUNT(*) FROM ListingRelationships lr WHERE lr.ListingId = l.Id) >= {minComps}
        GROUP BY {dateCast}
        ORDER BY OpDate";

    return await ExecuteQuery(db, sql, reader => new OpportunitiesByDayEntry(
        isSqlite ? reader.GetString(0) : reader.GetDateTime(0).ToString("yyyy-MM-dd"),
        reader.GetInt32(1)));
}
```

**Step 3: Wire it into GetOverview**

In the `GetOverview` method, add call after `cumulativeGrowth`:
```csharp
var opportunitiesByDay = await GetOpportunitiesByDay(db, filters);
```

Add to the `OverviewResponse` constructor after `CumulativeGrowth: cumulativeGrowth,`:
```csharp
OpportunitiesByDay: opportunitiesByDay,
```

**Step 4: Remove unused fields from response**

Remove from `OverviewResponse`:
- `IEnumerable<DaysToSellEntry> AvgDaysToSellByJob`
- `IEnumerable<RecentRunResponse> RecentRuns`

Remove from constructor in `GetOverview`:
- `AvgDaysToSellByJob:` line
- `RecentRuns:` line

Remove the `GetRecentRuns` private method entirely (lines 178-205).

Keep `RecentRunResponse` record — it may be used elsewhere.

**Step 5: Verify build**

Run: `dotnet build AIOMarketMaker.Api/AIOMarketMaker.Api.csproj`
Expected: Build succeeded

**Step 6: Test the endpoint**

Run: `curl -s "http://localhost:5000/api/overview?minComps=3" | python -m json.tool | grep -A2 opportunitiesByDay | head -20`
Expected: JSON array of `{ "date": "2026-02-XX", "count": N }` entries

**Step 7: Commit**

```bash
git add AIOMarketMaker.Api/Endpoints/OverviewEndpoints.cs
git commit -m "feat: add OpportunitiesByDay to overview API, remove unused fields"
```

---

### Task 2: Update HTML — remove charts, change grid to 3-2

**Files:**
- Modify: `AIOMarketMaker.Desktop/electron/src/index.html` (lines 78-116)

**Step 1: Replace the chart grid HTML**

Replace the entire `chart-grid` div (lines 78-116) with:
```html
          <!-- Charts: 3-2 layout -->
          <div class="chart-grid">
            <div class="chart-section">
              <h3>Cumulative Listings</h3>
              <div class="chart-container">
                <canvas id="cumulativeGrowthChart"></canvas>
              </div>
            </div>
            <div class="chart-section">
              <h3>New Opportunities</h3>
              <div class="chart-container">
                <canvas id="opportunityTrendChart"></canvas>
              </div>
            </div>
            <div class="chart-section">
              <h3>Avg Profit by Condition</h3>
              <div class="chart-container">
                <canvas id="avgProfitByConditionChart"></canvas>
              </div>
            </div>
            <div class="chart-section chart-wide">
              <h3>Top Jobs by Opportunities</h3>
              <div class="chart-container">
                <canvas id="topJobsChart"></canvas>
              </div>
            </div>
            <div class="chart-section chart-wide">
              <h3>Price vs Profit</h3>
              <div class="chart-container">
                <canvas id="priceVsProfitChart"></canvas>
              </div>
            </div>
          </div>
```

Note: `avgDaysToSellChart` and `recentScrapesChart` are gone. Bottom two charts get `chart-wide` class.

**Step 2: Commit**

```bash
git add AIOMarketMaker.Desktop/electron/src/index.html
git commit -m "feat: update dashboard to 3-2 chart layout"
```

---

### Task 3: Update CSS for 3-2 grid

**Files:**
- Modify: `AIOMarketMaker.Desktop/electron/src/styles.css` (lines 1043-1051)

**Step 1: Update chart-grid CSS**

Replace `.chart-grid` rule (lines 1043-1051) with:
```css
.chart-grid {
  flex: 1;
  display: grid;
  grid-template-columns: 1fr 1fr 1fr;
  grid-template-rows: 1fr 1fr;
  gap: 10px;
  margin-bottom: 0;
  min-height: 0;
}

.chart-grid .chart-wide {
  grid-column: span 1;
}

@media (min-width: 1px) {
  .chart-grid {
    grid-template-columns: repeat(3, 1fr);
  }
  .chart-grid .chart-section:nth-child(n+4) {
    grid-column: span 1;
  }
  /* Bottom row: 2 items in 3-col grid = use subgrid or manual placement */
  .chart-grid .chart-section:nth-child(4) {
    grid-column: 1 / 2;
  }
  .chart-grid .chart-section:nth-child(5) {
    grid-column: 2 / 4;
  }
}
```

Actually, simpler approach — since we have exactly 5 items in a 3-col grid, items 4 and 5 naturally go on row 2. We just need them to share the space evenly. Use a 6-column grid:

```css
.chart-grid {
  flex: 1;
  display: grid;
  grid-template-columns: repeat(6, 1fr);
  grid-template-rows: 1fr 1fr;
  gap: 10px;
  margin-bottom: 0;
  min-height: 0;
}

.chart-grid .chart-section {
  grid-column: span 2;
}

.chart-grid .chart-section:nth-child(4) {
  grid-column: 1 / 4;
}

.chart-grid .chart-section:nth-child(5) {
  grid-column: 4 / 7;
}
```

Top row: 3 items × span 2 = 6 cols. Bottom row: item 4 cols 1-3, item 5 cols 4-6. Both rows equal width.

**Step 2: Commit**

```bash
git add AIOMarketMaker.Desktop/electron/src/styles.css
git commit -m "feat: CSS 3-2 grid layout for dashboard charts"
```

---

### Task 4: Replace opportunity trend chart with line chart

**Files:**
- Modify: `AIOMarketMaker.Desktop/electron/src/app.js`

**Step 1: Replace `renderOpportunityTrendChart` method** (lines 552-596)

Replace with:
```javascript
    renderOpportunityTrendChart() {
      const canvas = document.getElementById('opportunityTrendChart');
      if (!canvas) { return; }

      if (this.overviewCharts.opportunityTrend) {
        this.overviewCharts.opportunityTrend.destroy();
      }

      const data = this.overviewData.opportunitiesByDay || [];
      this.overviewCharts.opportunityTrend = new Chart(canvas, {
        type: 'line',
        data: {
          labels: data.map(d => d.date),
          datasets: [{
            label: 'New Opportunities',
            data: data.map(d => d.count),
            borderColor: '#22c55e',
            backgroundColor: 'rgba(34, 197, 94, 0.1)',
            fill: true,
            tension: 0.3,
            pointRadius: data.length > 30 ? 0 : 3
          }]
        },
        options: {
          responsive: true,
          maintainAspectRatio: false,
          plugins: {
            legend: { display: false }
          },
          scales: {
            x: {
              ticks: { color: '#808080', maxTicksLimit: 10 },
              grid: { color: '#3c3c3c' }
            },
            y: {
              ticks: { color: '#808080' },
              grid: { color: '#3c3c3c' },
              beginAtZero: true
            }
          }
        }
      });
    },
```

**Step 2: Commit**

```bash
git add AIOMarketMaker.Desktop/electron/src/app.js
git commit -m "feat: replace recent scrapes with opportunities trend line chart"
```

---

### Task 5: Fix Avg Profit by Condition chart

**Files:**
- Modify: `AIOMarketMaker.Desktop/electron/src/app.js`

**Step 1: Update `renderAvgProfitByConditionChart`** (lines 598-648)

Replace with:
```javascript
    renderAvgProfitByConditionChart() {
      const canvas = document.getElementById('avgProfitByConditionChart');
      if (!canvas) { return; }

      if (this.overviewCharts.avgProfitByCondition) {
        this.overviewCharts.avgProfitByCondition.destroy();
      }

      const conditionLabels = {
        'USED': 'Used',
        'NEW': 'New',
        'FOR_PARTS_NOT_WORKING': 'For Parts',
        'GOOD_REFURBISHED': 'Good Refurb',
        'EXCELLENT_REFURBISHED': 'Excellent Refurb',
        'VERY_GOOD_REFURBISHED': 'VG Refurb',
        'OPENED_NEVER_USED': 'Open Box'
      };

      const data = (this.overviewData.avgProfitByCondition || [])
        .filter(d => d.condition && d.condition !== 'NULL');

      const colors = ['#4a9eff', '#22c55e', '#f59e0b', '#ef4444', '#a855f7', '#06b6d4'];
      this.overviewCharts.avgProfitByCondition = new Chart(canvas, {
        type: 'bar',
        data: {
          labels: data.map(d => conditionLabels[d.condition] || d.condition),
          datasets: [{
            data: data.map(d => d.avgProfit),
            backgroundColor: data.map((_, i) => colors[i % colors.length]),
            borderRadius: 4
          }]
        },
        options: {
          responsive: true,
          maintainAspectRatio: false,
          plugins: {
            legend: { display: false },
            tooltip: {
              callbacks: {
                afterLabel: (ctx) => {
                  const item = data[ctx.dataIndex];
                  return item ? `${item.count} opportunities` : '';
                }
              }
            }
          },
          scales: {
            x: {
              ticks: { color: '#e0e0e0' },
              grid: { display: false }
            },
            y: {
              ticks: {
                color: '#808080',
                callback: (v) => this.formatPrice(v, 'GBP')
              },
              grid: { color: '#3c3c3c' },
              beginAtZero: true
            }
          }
        }
      });
    },
```

**Step 2: Commit**

```bash
git add AIOMarketMaker.Desktop/electron/src/app.js
git commit -m "fix: readable condition labels, filter out NULL from profit chart"
```

---

### Task 6: Flip Top Jobs chart to vertical bars

**Files:**
- Modify: `AIOMarketMaker.Desktop/electron/src/app.js`

**Step 1: Update `renderTopJobsChart`** (lines 501-550)

Replace with:
```javascript
    renderTopJobsChart() {
      const canvas = document.getElementById('topJobsChart');
      if (!canvas) { return; }

      if (this.overviewCharts.topJobs) {
        this.overviewCharts.topJobs.destroy();
      }

      const data = this.overviewData.topJobsByOpportunities || [];
      const colors = ['#4a9eff', '#22c55e', '#f59e0b', '#ef4444', '#a855f7', '#06b6d4', '#ec4899', '#84cc16'];
      this.overviewCharts.topJobs = new Chart(canvas, {
        type: 'bar',
        data: {
          labels: data.map(d => this.truncate(d.searchTerm, 15)),
          datasets: [{
            data: data.map(d => d.opportunityCount),
            backgroundColor: data.map((_, i) => colors[i % colors.length]),
            borderRadius: 4
          }]
        },
        options: {
          responsive: true,
          maintainAspectRatio: false,
          plugins: {
            legend: { display: false },
            tooltip: {
              callbacks: {
                title: (ctx) => {
                  const item = data[ctx[0].dataIndex];
                  return item ? item.searchTerm : '';
                },
                afterLabel: (ctx) => {
                  const item = data[ctx.dataIndex];
                  return item ? `Profit: ${this.formatPrice(item.totalProfit, 'GBP')}` : '';
                }
              }
            }
          },
          scales: {
            x: {
              ticks: { color: '#e0e0e0', maxRotation: 45, minRotation: 45 },
              grid: { display: false }
            },
            y: {
              ticks: { color: '#808080' },
              grid: { color: '#3c3c3c' },
              beginAtZero: true,
              title: { display: true, text: 'Opportunities', color: '#808080' }
            }
          }
        }
      });
    },
```

Key changes: removed `indexAxis: 'y'`, truncate to 15 chars, rotated x-axis labels 45deg, tooltip title shows full name.

**Step 2: Commit**

```bash
git add AIOMarketMaker.Desktop/electron/src/app.js
git commit -m "feat: flip top jobs chart to vertical bars with rotated labels"
```

---

### Task 7: Fix Price vs Profit scatter plot

**Files:**
- Modify: `AIOMarketMaker.Desktop/electron/src/app.js`

**Step 1: Update `renderPriceVsProfitChart`** (lines 698-758)

Replace with:
```javascript
    renderPriceVsProfitChart() {
      const canvas = document.getElementById('priceVsProfitChart');
      if (!canvas) { return; }

      if (this.overviewCharts.priceVsProfit) {
        this.overviewCharts.priceVsProfit.destroy();
      }

      const data = this.overviewData.priceVsProfitPoints || [];
      const conditionColors = {
        'NEW': '#22c55e',
        'USED': '#4a9eff',
        'GOOD_REFURBISHED': '#f59e0b',
        'EXCELLENT_REFURBISHED': '#f59e0b',
        'VERY_GOOD_REFURBISHED': '#f59e0b',
        'OPENED_NEVER_USED': '#06b6d4',
        'FOR_PARTS_NOT_WORKING': '#ef4444'
      };
      this.overviewCharts.priceVsProfit = new Chart(canvas, {
        type: 'scatter',
        data: {
          datasets: [{
            data: data.map(d => ({ x: d.price, y: d.potentialProfit })),
            backgroundColor: data.map(d => conditionColors[d.condition] || '#a855f7'),
            pointRadius: 2,
            pointHoverRadius: 5
          }]
        },
        options: {
          responsive: true,
          maintainAspectRatio: false,
          plugins: {
            legend: { display: false },
            tooltip: {
              callbacks: {
                label: (ctx) => {
                  const item = data[ctx.dataIndex];
                  return `${item.condition || 'Unknown'}: Buy ${this.formatPrice(item.price, 'GBP')}, Profit ${this.formatPrice(item.potentialProfit, 'GBP')}`;
                }
              }
            }
          },
          scales: {
            x: {
              type: 'logarithmic',
              ticks: {
                color: '#808080',
                callback: (v) => `£${v}`
              },
              grid: { color: '#3c3c3c' },
              title: { display: true, text: 'Buy Price', color: '#808080' },
              max: 2000
            },
            y: {
              ticks: {
                color: '#808080',
                callback: (v) => `£${v}`
              },
              grid: { color: '#3c3c3c' },
              title: { display: true, text: 'Profit', color: '#808080' },
              beginAtZero: true,
              max: 500
            }
          }
        }
      });
    },
```

Key changes: `pointRadius: 2`, `type: 'logarithmic'` on X axis, `max: 2000` on X, `max: 500` on Y, condition colors match enum values.

**Step 2: Commit**

```bash
git add AIOMarketMaker.Desktop/electron/src/app.js
git commit -m "fix: scatter plot readability — smaller points, log scale, capped axes"
```

---

### Task 8: Remove dead chart code from app.js

**Files:**
- Modify: `AIOMarketMaker.Desktop/electron/src/app.js`

**Step 1: Remove `renderAvgDaysToSellChart` method** (entire method, ~lines 650-696)

**Step 2: Remove call from `renderCharts()`**

Change:
```javascript
    renderCharts() {
      this.renderCumulativeGrowthChart();
      this.renderOpportunityTrendChart();
      this.renderAvgProfitByConditionChart();
      this.renderTopJobsChart();
      this.renderAvgDaysToSellChart();
      this.renderPriceVsProfitChart();
    },
```

To:
```javascript
    renderCharts() {
      this.renderCumulativeGrowthChart();
      this.renderOpportunityTrendChart();
      this.renderAvgProfitByConditionChart();
      this.renderTopJobsChart();
      this.renderPriceVsProfitChart();
    },
```

**Step 3: Clean up overviewCharts init**

If `avgDaysToSell` is initialized in the data object, remove it.

**Step 4: Commit**

```bash
git add AIOMarketMaker.Desktop/electron/src/app.js
git commit -m "chore: remove dead avg days to sell chart code"
```

---

### Task 9: Visual verification

**Step 1: Restart API**

Run: `/setup-local-env restart`

**Step 2: Verify in browser**

Check:
- [ ] 3-2 grid layout renders correctly (3 top, 2 bottom)
- [ ] Opportunities trend line chart shows data points by day
- [ ] Avg Profit by Condition has readable labels, no NULL
- [ ] Top Jobs shows vertical bars with rotated labels
- [ ] Price vs Profit scatter has small points, log X scale, capped axes
- [ ] No console errors
- [ ] All tooltips work

**Step 3: Commit all remaining changes**

```bash
git add -A
git commit -m "feat: dashboard redesign — 3-2 layout, opportunities trend, chart fixes"
```
