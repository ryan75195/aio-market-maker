/**
 * Generate README screenshots from mocked Vue state.
 * Outputs to docs/screenshots/ at the repo root.
 *
 *   node tests/readme-screenshots.mjs
 */
import { _electron as electron } from 'playwright';
import path from 'path';
import fs from 'fs';
import { fileURLToPath } from 'url';

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const electronDir = path.resolve(__dirname, '..');
const repoRoot = path.resolve(electronDir, '..', '..');
const ssDir = path.join(repoRoot, 'docs', 'screenshots');
fs.mkdirSync(ssDir, { recursive: true });

function daysAgo(n) {
  const d = new Date();
  d.setDate(d.getDate() - n);
  return d.toISOString().slice(0, 10);
}

function isoMinusDays(n) {
  const d = new Date();
  d.setDate(d.getDate() - n);
  return d.toISOString();
}

const cumulativeGrowth = Array.from({ length: 60 }, (_, i) => ({
  date: daysAgo(60 - i),
  cumulative: Math.round(120000 + i * 4250 + Math.sin(i / 6) * 1800),
}));

const opportunitiesByDay = Array.from({ length: 30 }, (_, i) => ({
  date: daysAgo(30 - i),
  count: Math.round(180 + Math.sin(i / 3) * 60 + i * 4),
}));

const overviewMock = {
  totalListings: 374700,
  activeListings: 142318,
  soldListings: 232382,
  endedListings: 0,
  opportunities: 16310,
  aggregateProfit: 487921.5,
  lastScrape: { startedUtc: isoMinusDays(0.05), status: 'Completed' },
  cumulativeGrowth,
  opportunitiesByDay,
  avgProfitByCondition: [
    { condition: 'NEW', avgProfit: 38.45, count: 5421 },
    { condition: 'USED', avgProfit: 24.10, count: 7892 },
    { condition: 'OPENED_NEVER_USED', avgProfit: 31.80, count: 1184 },
    { condition: 'GOOD_REFURBISHED', avgProfit: 18.20, count: 894 },
    { condition: 'EXCELLENT_REFURBISHED', avgProfit: 22.65, count: 612 },
    { condition: 'FOR_PARTS_NOT_WORKING', avgProfit: 9.45, count: 307 },
  ],
  priceVsProfitPoints: Array.from({ length: 120 }, () => {
    const conds = ['NEW', 'USED', 'GOOD_REFURBISHED', 'EXCELLENT_REFURBISHED', 'OPENED_NEVER_USED'];
    const price = 30 + Math.random() * 750;
    const profit = (price * (0.04 + Math.random() * 0.08)) + (Math.random() - 0.3) * 20;
    return {
      price: Math.round(price * 100) / 100,
      estimatedProfit: Math.round(profit * 100) / 100,
      condition: conds[Math.floor(Math.random() * conds.length)],
    };
  }),
  topJobsByOpportunities: [
    { searchTerm: 'PlayStation 5 Slim', opportunityCount: 1240, totalProfit: 38420 },
    { searchTerm: 'Nintendo Switch OLED', opportunityCount: 982, totalProfit: 24180 },
    { searchTerm: 'iPhone 15 Pro', opportunityCount: 814, totalProfit: 31900 },
    { searchTerm: 'Lego Star Wars 75192', opportunityCount: 762, totalProfit: 19880 },
    { searchTerm: 'Pokemon 151 Booster Box', opportunityCount: 691, totalProfit: 17240 },
    { searchTerm: 'Sony WH-1000XM5', opportunityCount: 588, totalProfit: 14820 },
    { searchTerm: 'Steam Deck OLED 1TB', opportunityCount: 514, totalProfit: 22480 },
    { searchTerm: 'Nike Dunk Low Panda', opportunityCount: 472, totalProfit: 9120 },
    { searchTerm: 'Xbox Series X', opportunityCount: 421, totalProfit: 11240 },
    { searchTerm: 'Apple AirPods Pro 2', opportunityCount: 388, totalProfit: 8520 },
  ],
};

const opportunitiesMock = [
  { id: 101, listingId: '386421087412', searchTerm: 'PlayStation 5 Slim', title: 'Sony PlayStation 5 Slim Disc Edition 1TB Console — White (Sealed)', askPrice: 364.99, currency: 'GBP', condition: 'NEW', medianSoldPrice: 412.50, estimatedProfit: 32.84, soldComps: 18, avgDaysToSell: 7, cellKey: 'edition:disc|capacity:1tb', createdUtc: isoMinusDays(1), url: '#' },
  { id: 102, listingId: '266740123908', searchTerm: 'Pokemon 151 Booster Box', title: 'Pokémon TCG Scarlet & Violet 151 Booster Box English (Factory Sealed)', askPrice: 109.50, currency: 'GBP', condition: 'NEW', medianSoldPrice: 132.00, estimatedProfit: 13.92, soldComps: 24, avgDaysToSell: 8, cellKey: 'set:151|language:english', createdUtc: isoMinusDays(0), url: '#' },
  { id: 103, listingId: '305612498042', searchTerm: 'iPhone 15 Pro', title: 'Apple iPhone 15 Pro 256GB Natural Titanium - Unlocked - Excellent', askPrice: 698.00, currency: 'GBP', condition: 'EXCELLENT_REFURBISHED', medianSoldPrice: 789.99, estimatedProfit: 31.51, soldComps: 12, avgDaysToSell: 6, cellKey: 'capacity:256gb|color:nat-titanium', createdUtc: isoMinusDays(2), url: '#' },
  { id: 104, listingId: '276018342211', searchTerm: 'Steam Deck OLED 1TB', title: 'Valve Steam Deck OLED 1TB Limited Edition - Boxed Mint', askPrice: 549.00, currency: 'GBP', condition: 'OPENED_NEVER_USED', medianSoldPrice: 624.50, estimatedProfit: 26.74, soldComps: 9, avgDaysToSell: 11, cellKey: 'edition:limited|capacity:1tb', createdUtc: isoMinusDays(1), url: '#' },
  { id: 105, listingId: '155738401223', searchTerm: 'Lego Star Wars 75192', title: 'LEGO Star Wars UCS Millennium Falcon 75192 Brand New Sealed Retired', askPrice: 624.99, currency: 'GBP', condition: 'NEW', medianSoldPrice: 724.99, estimatedProfit: 36.18, soldComps: 15, avgDaysToSell: 5, cellKey: 'set:75192|state:retired', createdUtc: isoMinusDays(0), url: '#' },
  { id: 106, listingId: '364892015708', searchTerm: 'Sony WH-1000XM5', title: 'Sony WH-1000XM5 Wireless Noise Cancelling Headphones - Black', askPrice: 219.00, currency: 'GBP', condition: 'NEW', medianSoldPrice: 258.00, estimatedProfit: 14.94, soldComps: 31, avgDaysToSell: 10, cellKey: 'color:black', createdUtc: isoMinusDays(3), url: '#' },
  { id: 107, listingId: '276240187521', searchTerm: 'Nintendo Switch OLED', title: 'Nintendo Switch OLED Console White - Joy-Cons - Boxed', askPrice: 219.99, currency: 'GBP', condition: 'GOOD_REFURBISHED', medianSoldPrice: 254.00, estimatedProfit: 9.32, soldComps: 22, avgDaysToSell: 12, cellKey: 'color:white|model:oled', createdUtc: isoMinusDays(2), url: '#' },
  { id: 108, listingId: '186091523014', searchTerm: 'Nike Dunk Low Panda', title: 'Nike Dunk Low Panda Black White DD1391-100 Mens UK 9 Brand New', askPrice: 92.00, currency: 'GBP', condition: 'NEW', medianSoldPrice: 118.00, estimatedProfit: 6.18, soldComps: 14, avgDaysToSell: 7, cellKey: 'colorway:panda|size:uk-9', createdUtc: isoMinusDays(1), url: '#' },
];

const marketsJobs = [
  { jobId: 1, searchTerm: 'PlayStation 5 Slim', categories: ['Consoles'], soldCount: 2978, activeCount: 1842, salesPerDay: 14.6, sellThrough: 62, avgDaysToSell: 9, avgAskPrice: 384, medianSoldPrice: 412, p25SoldPrice: 359, p75SoldPrice: 459 },
  { jobId: 2, searchTerm: 'Nintendo Switch OLED', categories: ['Consoles'], soldCount: 2378, activeCount: 1564, salesPerDay: 11.8, sellThrough: 60, avgDaysToSell: 11, avgAskPrice: 234, medianSoldPrice: 254, p25SoldPrice: 224, p75SoldPrice: 282 },
  { jobId: 3, searchTerm: 'iPhone 15 Pro', categories: ['Phones'], soldCount: 2112, activeCount: 1102, salesPerDay: 10.5, sellThrough: 66, avgDaysToSell: 8, avgAskPrice: 712, medianSoldPrice: 789, p25SoldPrice: 729, p75SoldPrice: 849 },
  { jobId: 4, searchTerm: 'Lego Star Wars 75192', categories: ['Lego'], soldCount: 2098, activeCount: 882, salesPerDay: 10.4, sellThrough: 70, avgDaysToSell: 7, avgAskPrice: 642, medianSoldPrice: 724, p25SoldPrice: 689, p75SoldPrice: 764 },
  { jobId: 5, searchTerm: 'Pokemon 151 Booster Box', categories: ['TCG'], soldCount: 1720, activeCount: 1042, salesPerDay: 8.6, sellThrough: 62, avgDaysToSell: 10, avgAskPrice: 116, medianSoldPrice: 132, p25SoldPrice: 119, p75SoldPrice: 142 },
  { jobId: 6, searchTerm: 'Sony WH-1000XM5', categories: ['Audio'], soldCount: 1398, activeCount: 1014, salesPerDay: 7.0, sellThrough: 58, avgDaysToSell: 12, avgAskPrice: 232, medianSoldPrice: 258, p25SoldPrice: 235, p75SoldPrice: 279 },
  { jobId: 7, searchTerm: 'Steam Deck OLED 1TB', categories: ['Consoles'], soldCount: 1278, activeCount: 824, salesPerDay: 6.4, sellThrough: 61, avgDaysToSell: 11, avgAskPrice: 568, medianSoldPrice: 624, p25SoldPrice: 579, p75SoldPrice: 668 },
  { jobId: 8, searchTerm: 'Nike Dunk Low Panda', categories: ['Sneakers'], soldCount: 1282, activeCount: 612, salesPerDay: 6.4, sellThrough: 68, avgDaysToSell: 8, avgAskPrice: 102, medianSoldPrice: 118, p25SoldPrice: 109, p75SoldPrice: 128 },
];

const marketsMock = {
  jobs: marketsJobs,
  allCategories: ['Consoles', 'Phones', 'Lego', 'TCG', 'Audio', 'Sneakers'],
};

const marketsDrillSelected = {
  jobId: 1,
  searchTerm: 'PlayStation 5 Slim',
  categories: ['Consoles'],
  soldCount: 2978,
  activeCount: 1842,
  salesPerDay: 14.6,
  sellThrough: 62,
  avgDaysToSell: 9,
  avgAskPrice: 384,
  medianSoldPrice: 412,
};

const marketsDrillListings = [
  { id: 5001, title: 'Sony PlayStation 5 Slim Disc Edition 1TB Console - White (Sealed)', price: 364.99, listingStatus: 'Active', condition: 'New', daysOnMarket: 2, createdUtc: isoMinusDays(2) },
  { id: 5002, title: 'Sony PS5 Slim 1TB Disc Edition - Boxed - Excellent', price: 339.00, listingStatus: 'Sold', condition: 'Used', daysOnMarket: 5, endDateUtc: isoMinusDays(1) },
  { id: 5003, title: 'PlayStation 5 Slim Digital Edition 1TB - Brand New Sealed UK', price: 339.99, listingStatus: 'Active', condition: 'New', daysOnMarket: 1, createdUtc: isoMinusDays(1) },
  { id: 5004, title: 'Sony PS5 Slim Disc 1TB Console - Used - Boxed Complete', price: 309.50, listingStatus: 'Sold', condition: 'Used', daysOnMarket: 8, endDateUtc: isoMinusDays(2) },
  { id: 5005, title: 'PlayStation 5 Slim 1TB Digital Edition White (Sealed)', price: 332.00, listingStatus: 'Active', condition: 'New', daysOnMarket: 4, createdUtc: isoMinusDays(4) },
  { id: 5006, title: 'PS5 Slim Disc 1TB Console + Extra DualSense Controller - New', price: 419.99, listingStatus: 'Sold', condition: 'New', daysOnMarket: 3, endDateUtc: isoMinusDays(0) },
  { id: 5007, title: 'Sony PlayStation 5 Slim Disc 1TB - Excellent Refurbished', price: 309.00, listingStatus: 'Sold', condition: 'Excellent Refurb', daysOnMarket: 6, endDateUtc: isoMinusDays(3) },
  { id: 5008, title: 'PS5 Slim Disc 1TB White - New Sealed Boxed Receipt', price: 369.50, listingStatus: 'Active', condition: 'New', daysOnMarket: 1, createdUtc: isoMinusDays(1) },
];

const marketsListingStats = {
  totalCount: 4820,
  soldCount: 2978,
  activeCount: 1842,
  sellThrough: 62,
  avgDaysToSell: 9,
  avgPrice: 384.20,
  minPrice: 289.00,
  maxPrice: 519.99,
};

async function captureView(page, viewName, filename, mocks) {
  await page.evaluate(({ viewName, mocks }) => {
    const vueApp = document.querySelector('#app').__vue_app__;
    const vm = vueApp._instance.proxy;

    vm.currentView = viewName;
    Object.assign(vm, mocks);
  }, { viewName, mocks });

  await page.waitForTimeout(800);

  // For overview, manually trigger Chart.js render after state injection.
  if (viewName === 'overview') {
    await page.evaluate(() => {
      const vueApp = document.querySelector('#app').__vue_app__;
      const vm = vueApp._instance.proxy;
      vm.$nextTick(() => {
        if (typeof vm.renderCharts === 'function') { vm.renderCharts(); }
      });
    });
    await page.waitForTimeout(1500);
  }

  await page.screenshot({ path: path.join(ssDir, filename), fullPage: true });
  console.log(`✓ ${filename}`);
}

async function main() {
  const app = await electron.launch({
    args: [path.join(electronDir, 'main.js')],
    env: { ...process.env, NODE_ENV: 'test' },
  });

  const page = await app.firstWindow();
  page.setDefaultTimeout(30000);
  await page.setViewportSize({ width: 1440, height: 900 });
  await page.waitForTimeout(2500);

  // Suppress error/loading banners that depend on a live API
  await page.evaluate(() => {
    const vueApp = document.querySelector('#app').__vue_app__;
    const vm = vueApp._instance.proxy;
    vm.config = vm.config || {};
    vm.configError = null;
    vm.toast = null;
  });

  // 1. Overview — KPIs + charts
  await captureView(page, 'overview', '01-overview.png', {
    overviewLoading: false,
    overviewError: null,
    overviewData: overviewMock,
  });

  // 2. Opportunities — pricing table
  await captureView(page, 'opportunities', '02-opportunities.png', {
    opportunitiesLoading: false,
    opportunities: opportunitiesMock,
    opportunityTotalCount: 16310,
    opportunityTotalPages: 2039,
    opportunityPage: 1,
    jobs: marketsJobs.map(m => ({ id: m.jobId, searchTerm: m.searchTerm, isEnabled: true })),
    categories: [],
  });

  // 3. Markets — list of search-term jobs with sell-through stats
  await captureView(page, 'markets', '03-markets-list.png', {
    marketsLoading: false,
    marketsError: null,
    marketsData: marketsMock,
    marketsSelected: null,
  });

  // 4. Markets drill-in — single job with listings table
  await captureView(page, 'markets', '04-markets-drilldown.png', {
    marketsLoading: false,
    marketsError: null,
    marketsData: marketsMock,
    marketsSelected: marketsDrillSelected,
    marketsListings: marketsDrillListings,
    marketsListingStats,
    marketsListingTotal: marketsDrillListings.length,
    marketsListingsLoading: false,
    marketsShowFilters: false,
    marketsRegex: '',
    marketsStatusFilter: '',
  });

  await app.close();
  console.log(`\nScreenshots saved to ${ssDir}`);
}

main().catch(err => { console.error(err); process.exit(1); });
