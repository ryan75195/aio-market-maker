import { describe, it, expect, beforeAll, afterAll } from 'vitest';
import { _electron as electron } from 'playwright';
import path from 'path';
import { fileURLToPath } from 'url';

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const electronDir = path.resolve(__dirname, '..');
const ssDir = path.join(electronDir, 'tests', 'screenshots');

let electronApp;
let page;

beforeAll(async () => {
  electronApp = await electron.launch({
    args: [path.join(electronDir, 'main.js')],
    env: { ...process.env, NODE_ENV: 'test' },
  });
  page = await electronApp.firstWindow();
  // Wait for Vue to mount
  await page.waitForTimeout(2000);
}, 30000);

afterAll(async () => {
  if (electronApp) {
    await electronApp.close();
  }
});

describe('app launch', () => {
  it('should show the Market Maker title', async () => {
    const title = await page.title();
    expect(title).toContain('Market Maker');
  });

  it('should render the sidebar nav buttons', async () => {
    const buttons = page.locator('.sidebar button');
    const count = await buttons.count();
    expect(count).toBeGreaterThanOrEqual(4);

    const texts = [];
    for (let i = 0; i < count; i++) {
      texts.push(await buttons.nth(i).innerText());
    }
    expect(texts).toContain('Overview');
    expect(texts).toContain('Index');
    expect(texts).toContain('Settings');
  });

  it('should show Local connection indicator', async () => {
    const local = page.locator('text=Local');
    expect(await local.count()).toBeGreaterThan(0);
  });
});

describe('Index / batch history view', () => {
  it('should navigate to Index and load batch list', async () => {
    // Click the Index nav button
    await page.locator('.sidebar button', { hasText: 'Index' }).click();
    // Wait for API response
    await page.waitForTimeout(3000);

    await page.screenshot({ path: path.join(ssDir, '01-index-view.png'), fullPage: true });

    const bodyText = await page.locator('body').innerText();
    const hasContent = bodyText.includes('Manual') || bodyText.includes('Nightly') ||
      bodyText.includes('Completed') || bodyText.includes('PartialFailure') ||
      bodyText.includes('Running');
    expect(hasContent).toBe(true);
  });

  it('should show batch progress bar or status for most recent batch', async () => {
    // The batch list should have progress info
    const bodyText = await page.locator('body').innerText();
    // Should show counts like "1,426/2,440", status badges, or "processed" text
    const upper = bodyText.toUpperCase();
    const hasProgress = bodyText.match(/[\d,]+\/[\d,]+/) ||
      upper.includes('COMPLETED') || upper.includes('PARTIALFAILURE') ||
      upper.includes('RUNNING') || bodyText.includes('processed');
    expect(hasProgress).toBeTruthy();
  });
});

describe('batch detail view', () => {
  it('should open batch detail when clicking a batch row', async () => {
    // Click the first batch row (table row or clickable element)
    const batchRow = page.locator('tr.batch-row, .batch-card, tbody tr').first();
    if (await batchRow.count() > 0) {
      await batchRow.click();
    }
    await page.waitForTimeout(3000);

    await page.screenshot({ path: path.join(ssDir, '02-batch-detail.png'), fullPage: true });

    // Should show "Back to Batches" link and job names
    const bodyText = await page.locator('body').innerText();
    expect(bodyText.includes('Back to Batches') || bodyText.includes('PlayStation') || bodyText.includes('Mac Mini')).toBe(true);
  });

  it('should show progress bars only for runs with new listings expected', async () => {
    const progressBars = page.locator('.progress-bar:not(.searching)');
    const progressCount = await progressBars.count();

    const noNewBadges = page.locator('.status-badge:not(.searching)', { hasText: 'No new' });
    const noNewCount = await noNewBadges.count();

    const completedRuns = page.locator('text=COMPLETED');
    const completedCount = await completedRuns.count();

    await page.screenshot({ path: path.join(ssDir, '03-run-progress.png'), fullPage: true });

    // Should have some combination of progress bars, "No new" badges, or completed runs
    expect(progressCount + noNewCount + completedCount).toBeGreaterThan(0);
  });

  it('should have matching progress bar fill and text', async () => {
    const progressBars = page.locator('.progress-bar:not(.searching)');
    const count = await progressBars.count();

    const mismatches = [];
    for (let i = 0; i < Math.min(count, 15); i++) {
      const bar = progressBars.nth(i);
      const textEl = bar.locator('.progress-text');
      if (await textEl.count() === 0) { continue; }

      const text = await textEl.innerText();
      const match = text.match(/([\d,]+)\s*\/\s*([\d,]+)/);
      if (!match) { continue; }

      const done = parseInt(match[1].replace(/,/g, ''));
      const total = parseInt(match[2].replace(/,/g, ''));

      let expectedPercent;
      if (total <= 0) {
        expectedPercent = done > 0 ? 100 : 0;
      } else {
        expectedPercent = Math.min(100, Math.round((done / total) * 100));
      }

      const fill = bar.locator('.progress-fill');
      const style = await fill.getAttribute('style');
      const widthMatch = style?.match(/width:\s*([\d.]+)%/);
      if (!widthMatch) { continue; }

      const actualPercent = Math.round(parseFloat(widthMatch[1]));
      if (actualPercent !== expectedPercent) {
        mismatches.push({ text, expectedPercent, actualPercent });
      }
    }

    expect(mismatches).toEqual([]);
  });

  it('should show run-level columns: active, sold, upd, skip, fail', async () => {
    const bodyText = await page.locator('body').innerText();
    // Column headers
    expect(bodyText).toContain('ACTIVE');
    expect(bodyText).toContain('SOLD');
    expect(bodyText).toContain('UPD');
    expect(bodyText).toContain('SKIP');
    expect(bodyText).toContain('FAIL');
  });
});

describe('stats banner', () => {
  it('should display stats banner with phase, runtime, and progress', async () => {
    // Go back to batch list to see stats banner
    const backBtn = page.locator('text=Back to Batches');
    if (await backBtn.count() > 0) {
      await backBtn.click();
      await page.waitForTimeout(1000);
    }

    await page.screenshot({ path: path.join(ssDir, '04-stats-banner.png'), fullPage: true });

    const bodyText = await page.locator('body').innerText();
    // Should contain time-related text or progress numbers
    const hasStats = bodyText.includes('Runtime') || bodyText.includes('Progress') ||
      bodyText.includes('ETA') || bodyText.includes('Jobs') ||
      bodyText.match(/\d+h\s/) || bodyText.match(/\d+m\s/) ||
      bodyText.match(/[\d,]+\/[\d,]+/);
    expect(hasStats).toBeTruthy();
  });

  it('should show stats banner progress matching batch bar progress', async () => {
    // Both should use the same done/total formula
    const statsText = await page.locator('.stats-banner, .stat').allInnerTexts();
    const batchBar = page.locator('.progress-bar:not(.searching) .progress-text');

    if (await batchBar.count() > 0) {
      const barText = await batchBar.first().innerText();
      const barMatch = barText.match(/([\d,]+)\/([\d,]+)/);

      if (barMatch) {
        // Find the same numbers in the stats banner
        const statsJoined = statsText.join(' ');
        const sameNumbers = statsJoined.includes(barMatch[1]) && statsJoined.includes(barMatch[2]);
        // If stats banner is visible and shows progress, it should match
        if (statsJoined.match(/[\d,]+\/[\d,]+/)) {
          expect(sameNumbers).toBe(true);
        }
      }
    }
  });
});
