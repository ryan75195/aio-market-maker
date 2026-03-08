/**
 * Take a screenshot of the chat panel with mock tool calls + filtering conversation.
 * For social media / tweet attachment.
 */
import { _electron as electron } from 'playwright';
import path from 'path';
import fs from 'fs';
import { fileURLToPath } from 'url';

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const electronDir = path.resolve(__dirname, '..');
const ssDir = path.join(electronDir, 'tests', 'screenshots');
fs.mkdirSync(ssDir, { recursive: true });

async function main() {
  const app = await electron.launch({
    args: [path.join(electronDir, 'main.js')],
    env: { ...process.env, NODE_ENV: 'test' },
  });

  const page = await app.firstWindow();
  page.setDefaultTimeout(30000);

  // Wait for Vue to mount
  await page.waitForTimeout(3000);

  // Inject all mock data into Vue to simulate a markets drill-in with chat open
  await page.evaluate(() => {
    const vueApp = document.querySelector('#app').__vue_app__;
    const vm = vueApp._instance.proxy;

    // Core view state
    vm.currentView = 'markets';
    vm.marketsLoading = false;
    vm.marketsError = null;

    // Selected job (triggers drill-in view)
    vm.marketsSelected = {
      jobId: 1,
      searchTerm: 'Pokemon Booster Box',
      totalListings: 847,
      activeListings: 312,
      soldListings: 535,
      salesPerDay: 8.2,
      sellThrough: 63,
      avgDaysToSell: 11,
      avgAskPrice: 108.42,
      medianSoldPrice: 99.99,
    };

    // Listing stats for KPI strip (field names must match computed property)
    vm.marketsListingStats = {
      totalCount: 94,
      soldCount: 67,
      activeCount: 27,
      sellThrough: 71,
      avgDaysToSell: 9,
      avgPrice: 121.50,
      minPrice: 89.99,
      maxPrice: 189.00,
    };
    vm.marketsListingTotal = 94;
    vm.marketsListingsLoading = false;

    // Show filters expanded with a regex - not expanded to keep screenshot cleaner
    vm.marketsShowFilters = false;
    vm.marketsRegex = '151.*booster\\s*box(?!.*(bundle|pack|etb|tin))';
    vm.marketsStatusFilter = '';

    // Mock listings (filtered 151 booster boxes)
    vm.marketsListings = [
      { id: 1, title: 'Pokemon Scarlet & Violet 151 Booster Box Sealed', price: 124.99, listingStatus: 'Active', condition: 'New', daysOnMarket: 3, createdUtc: '2026-02-27T10:00:00Z' },
      { id: 2, title: 'Pokemon 151 Booster Box - English Sealed New', price: 119.50, listingStatus: 'Sold', condition: 'New', daysOnMarket: 5, endDateUtc: '2026-02-25T14:30:00Z' },
      { id: 3, title: 'Pokemon SV 151 Booster Box Factory Sealed', price: 117.00, listingStatus: 'Sold', condition: 'New', daysOnMarket: 2, endDateUtc: '2026-02-28T09:15:00Z' },
      { id: 4, title: 'Scarlet Violet 151 Booster Box English', price: 121.99, listingStatus: 'Active', condition: 'New', daysOnMarket: 1, createdUtc: '2026-03-01T11:00:00Z' },
      { id: 5, title: 'Pokemon 151 Booster Box Sealed UK Stock', price: 115.00, listingStatus: 'Sold', condition: 'New', daysOnMarket: 7, endDateUtc: '2026-02-23T16:00:00Z' },
      { id: 6, title: 'Pokemon Scarlet & Violet 151 Booster Box', price: 129.99, listingStatus: 'Active', condition: 'New', daysOnMarket: 4, createdUtc: '2026-02-26T08:45:00Z' },
      { id: 7, title: 'Pokemon 151 Booster Box Collection Sealed', price: 118.50, listingStatus: 'Sold', condition: 'New', daysOnMarket: 3, endDateUtc: '2026-02-27T19:30:00Z' },
      { id: 8, title: 'SV 151 Pokemon Booster Box - New Sealed', price: 122.00, listingStatus: 'Sold', condition: 'New', daysOnMarket: 6, endDateUtc: '2026-02-24T12:00:00Z' },
    ];

    // Open chat panel in expanded mode
    vm.marketsChatOpen = true;
    vm.marketsChatExpanded = true;

    // Inject a realistic chat conversation with tool calls
    const now = Date.now();
    vm.marketsChatMessages = [
      {
        role: 'assistant',
        text: "I can help you explore Pokemon Booster Box listings. What would you like to filter?",
        time: new Date(now - 120000),
      },
      {
        role: 'user',
        text: "Show me only Scarlet & Violet 151 booster boxes, filter out bundles and single packs",
        time: new Date(now - 110000),
      },
      {
        role: 'tool',
        toolName: 'query_listings',
        text: '847 listings (312 active, 535 sold)',
        status: 'done',
        time: new Date(now - 105000),
      },
      {
        role: 'tool',
        toolName: 'set_filters',
        text: 'Filters applied',
        status: 'done',
        time: new Date(now - 100000),
      },
      {
        role: 'assistant',
        text: "Found 94 Scarlet & Violet 151 booster boxes. Filtered out bundles, packs, and ETBs. Avg sold price is \u00a3121.50 across 67 sales.",
        time: new Date(now - 95000),
      },
      {
        role: 'user',
        text: "Are there any outliers pulling the average up?",
        time: new Date(now - 80000),
      },
      {
        role: 'tool',
        toolName: 'sample_listings',
        text: 'Sampled 10 of 94 listings',
        status: 'done',
        time: new Date(now - 75000),
      },
      {
        role: 'assistant',
        text: "3 listings above \u00a3200 are graded/special editions. Excluding those, median sold price is \u00a3117.00. Clean cluster.",
        time: new Date(now - 70000),
      },
      {
        role: 'user',
        text: "Save these filters for new listings",
        time: new Date(now - 50000),
      },
      {
        role: 'tool',
        toolName: 'set_filters',
        text: 'Filters applied',
        status: 'done',
        time: new Date(now - 45000),
      },
      {
        role: 'assistant',
        text: "Filters saved. New listings matching `151.*booster box` will be grouped into this category automatically.",
        time: new Date(now - 40000),
      },
    ];

    vm.marketsChatLoading = false;
    vm.marketsChatToolStatus = null;
  });

  await page.waitForTimeout(1000);

  // Scroll chat to bottom
  await page.evaluate(() => {
    const chatMessages = document.querySelector('.chat-messages');
    if (chatMessages) {
      chatMessages.scrollTop = chatMessages.scrollHeight;
    }
  });

  await page.waitForTimeout(500);

  // Take screenshot of the full window
  const fullPath = path.join(ssDir, 'chat-filtering-demo.png');
  await page.screenshot({ path: fullPath });
  console.log(`Full screenshot saved: ${fullPath}`);

  // Also take a cropped screenshot of just the chat panel area
  const chatPanel = page.locator('.chat-panel.expanded');
  if (await chatPanel.count() > 0) {
    const chatPath = path.join(ssDir, 'chat-filtering-chat-only.png');
    await chatPanel.screenshot({ path: chatPath });
    console.log(`Chat panel screenshot saved: ${chatPath}`);
  }

  await app.close();
  console.log('Done.');
}

main().catch(err => {
  console.error(err);
  process.exit(1);
});
