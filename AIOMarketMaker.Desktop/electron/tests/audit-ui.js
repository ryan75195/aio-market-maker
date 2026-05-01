/**
 * Take screenshots of every UI view for visual audit.
 */
import { _electron as electron } from 'playwright';
import path from 'path';
import fs from 'fs';
import { fileURLToPath } from 'url';

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const electronDir = path.resolve(__dirname, '..');
const ssDir = path.join(electronDir, 'tests', 'screenshots', 'audit');
fs.mkdirSync(ssDir, { recursive: true });

async function main() {
  const app = await electron.launch({
    args: [path.join(electronDir, 'main.js')],
    env: { ...process.env, NODE_ENV: 'test' },
  });

  const page = await app.firstWindow();
  page.setDefaultTimeout(30000);
  await page.waitForTimeout(3000);

  // 1. Overview page
  await page.screenshot({ path: path.join(ssDir, '01-overview.png'), fullPage: true });
  console.log('1. Overview captured');

  // 2. Index - batch list
  await page.locator('.sidebar button', { hasText: 'Index' }).click();
  await page.waitForTimeout(3000);
  await page.screenshot({ path: path.join(ssDir, '02-index-batch-list.png'), fullPage: true });
  console.log('2. Index batch list captured');

  // 3. Click first batch to see detail
  const firstRow = page.locator('tbody tr').first();
  if (await firstRow.count() > 0) {
    await firstRow.click();
    await page.waitForTimeout(3000);
    await page.screenshot({ path: path.join(ssDir, '03-batch-detail-top.png'), fullPage: true });
    console.log('3. Batch detail (top) captured');

    // 4. Scroll down to see more runs
    await page.evaluate(() => {
      const content = document.querySelector('.index-panel-content');
      if (content) { content.scrollTop = content.scrollHeight / 2; }
    });
    await page.waitForTimeout(500);
    await page.screenshot({ path: path.join(ssDir, '04-batch-detail-mid.png'), fullPage: true });
    console.log('4. Batch detail (middle) captured');

    // 5. Scroll to bottom
    await page.evaluate(() => {
      const content = document.querySelector('.index-panel-content');
      if (content) { content.scrollTop = content.scrollHeight; }
    });
    await page.waitForTimeout(500);
    await page.screenshot({ path: path.join(ssDir, '05-batch-detail-bottom.png'), fullPage: true });
    console.log('5. Batch detail (bottom) captured');

    // 6. Back to batches
    const backBtn = page.locator('text=Back to Batches');
    if (await backBtn.count() > 0) {
      await backBtn.click();
      await page.waitForTimeout(1000);
    }
  }

  // 7. Settings page
  await page.locator('.sidebar button', { hasText: 'Settings' }).click();
  await page.waitForTimeout(2000);
  await page.screenshot({ path: path.join(ssDir, '06-settings.png'), fullPage: true });
  console.log('6. Settings captured');

  // 8. Opportunities page
  await page.locator('.sidebar button', { hasText: 'Opportunities' }).click();
  await page.waitForTimeout(2000);
  await page.screenshot({ path: path.join(ssDir, '07-opportunities.png'), fullPage: true });
  console.log('7. Opportunities captured');

  // 9. Go back to Index, click a completed batch to see final state
  await page.locator('.sidebar button', { hasText: 'Index' }).click();
  await page.waitForTimeout(2000);
  // Click second batch (likely completed)
  const secondRow = page.locator('tbody tr').nth(1);
  if (await secondRow.count() > 0) {
    await secondRow.click();
    await page.waitForTimeout(3000);
    await page.screenshot({ path: path.join(ssDir, '08-completed-batch.png'), fullPage: true });
    console.log('8. Completed batch detail captured');
  }

  await app.close();
  console.log('Done - screenshots in tests/screenshots/audit/');
}

main().catch(err => {
  console.error(err.message);
  process.exit(1);
});
