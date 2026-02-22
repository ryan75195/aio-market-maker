import { chromium } from 'playwright';
import { fileURLToPath } from 'url';
import path from 'path';

const __dirname = path.dirname(fileURLToPath(import.meta.url));

async function capture() {
  const browser = await chromium.launch();
  const context = await browser.newContext({
    viewport: { width: 1280, height: 800 },
    deviceScaleFactor: 2,  // retina quality
  });

  const pages = [
    { name: 'opportunities', file: 'mockups/opportunities.html' },
    { name: 'dashboard', file: 'mockups/dashboard.html' },
    { name: 'history', file: 'mockups/history.html' },
    { name: 'listing-detail', file: 'mockups/listing-detail.html' },
  ];

  for (const { name, file } of pages) {
    const page = await context.newPage();
    await page.goto(`file://${path.join(__dirname, file).replace(/\\/g, '/')}`);
    await page.waitForTimeout(500);

    // Screenshot just the content area (not the whole browser)
    await page.screenshot({
      path: path.join(__dirname, '..', 'public', 'screenshots', `${name}.png`),
      clip: { x: 0, y: 0, width: 1280, height: 800 },
    });

    console.log(`Captured: ${name}.png`);
    await page.close();
  }

  await browser.close();
  console.log('All screenshots captured!');
}

capture();
