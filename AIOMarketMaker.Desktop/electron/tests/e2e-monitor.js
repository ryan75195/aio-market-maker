/**
 * E2E test: Monitor a running scrape batch via the UI.
 *
 * Usage: node tests/e2e-monitor.js
 *
 * Launches Electron, navigates to the most recent batch, and polls the UI
 * every 10 seconds to verify progress is updating. Takes screenshots at
 * checkpoints and reports issues.
 */

import { _electron as electron } from 'playwright';
import path from 'path';
import fs from 'fs';
import { fileURLToPath } from 'url';

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const electronDir = path.resolve(__dirname, '..');
const ssDir = path.join(electronDir, 'tests', 'screenshots', 'monitor');

fs.mkdirSync(ssDir, { recursive: true });

const POLL_INTERVAL_MS = 10_000;
const MAX_POLLS = 300;
const START_NEW = process.argv.includes('--start');

function log(msg) {
  const ts = new Date().toLocaleTimeString('en-GB');
  console.log(`[${ts}] ${msg}`);
}

function logError(msg) {
  const ts = new Date().toLocaleTimeString('en-GB');
  console.error(`[${ts}] ERROR: ${msg}`);
}

async function sleep(ms) {
  return new Promise(r => setTimeout(r, ms));
}

async function main() {
  log('Launching Electron app...');
  const app = await electron.launch({
    args: [path.join(electronDir, 'main.js')],
    env: { ...process.env, NODE_ENV: 'test' },
  });

  const page = await app.firstWindow();
  page.setDefaultTimeout(30000);
  await page.waitForTimeout(5000);
  log('App launched. Taking initial screenshot...');
  await page.screenshot({ path: path.join(ssDir, 'debug-launch.png'), fullPage: true });

  // Try to find and click the Index button
  const indexBtn = page.locator('.sidebar button', { hasText: 'Index' });
  const btnCount = await indexBtn.count();
  log(`Found ${btnCount} Index button(s). Clicking...`);
  if (btnCount > 0) {
    await indexBtn.click();
  } else {
    // Fallback: try any button with Index text
    log('Sidebar button not found, trying alternative selectors...');
    const altBtn = page.locator('button:has-text("Index"), a:has-text("Index"), [class*=nav]:has-text("Index")');
    if (await altBtn.count() > 0) {
      await altBtn.first().click();
    } else {
      log('WARNING: Could not find Index button, proceeding anyway');
    }
  }
  await page.waitForTimeout(3000);

  if (START_NEW) {
    log('Clicking "Start Scrape"...');
    const startBtn = page.locator('button', { hasText: 'Start Scrape' });
    if (await startBtn.count() > 0) {
      await startBtn.click();
      await page.waitForTimeout(5000);
      log('Batch started');
    }
  }

  await page.screenshot({ path: path.join(ssDir, '00-index.png'), fullPage: true });

  // Open the first batch row
  await page.waitForTimeout(1000);
  const batchRow = page.locator('tbody tr').first();
  if (await batchRow.count() > 0) {
    await batchRow.click();
    await page.waitForTimeout(2000);
    log('Opened batch detail view');
  }

  await page.screenshot({ path: path.join(ssDir, '01-detail-initial.png'), fullPage: true });

  // --- Monitoring loop ---
  let prevSnapshot = null;
  let stalledCount = 0;
  let pollNum = 0;
  const issues = [];
  let lastPhase = null;

  for (pollNum = 0; pollNum < MAX_POLLS; pollNum++) {
    await sleep(POLL_INTERVAL_MS);

    let snapshot;
    try {
      snapshot = await captureState(page);
    } catch (err) {
      logError(`captureState failed: ${err.message}`);
      // Try to recover by waiting and retrying
      await sleep(3000);
      try {
        snapshot = await captureState(page);
      } catch (err2) {
        logError(`captureState retry failed: ${err2.message}`);
        continue; // skip this poll
      }
    }

    // Take screenshot on phase/status changes or every 30th poll (5 min)
    const phaseChanged = lastPhase !== snapshot.batchPhase;
    const statusChanged = prevSnapshot && snapshot.batchStatus !== prevSnapshot.batchStatus;
    if (pollNum % 30 === 0 || phaseChanged || statusChanged) {
      const ssFile = `poll-${String(pollNum).padStart(3, '0')}.png`;
      await page.screenshot({ path: path.join(ssDir, ssFile), fullPage: true });
    }
    if (phaseChanged) { lastPhase = snapshot.batchPhase; }

    // Stall detection: check ALL status fields
    if (prevSnapshot) {
      const changed =
        snapshot.runStatuses.completed !== prevSnapshot.runStatuses.completed ||
        snapshot.runStatuses.running !== prevSnapshot.runStatuses.running ||
        snapshot.runStatuses.queued !== prevSnapshot.runStatuses.queued ||
        snapshot.runStatuses.searching !== prevSnapshot.runStatuses.searching ||
        snapshot.runStatuses.updating !== prevSnapshot.runStatuses.updating ||
        snapshot.progressText !== prevSnapshot.progressText;

      if (!changed) {
        stalledCount++;
        if (stalledCount >= 12) { // 2 minutes truly stalled
          const msg = `Stalled for ${stalledCount * 10}s — no status changes`;
          logError(msg);
          issues.push(`Poll ${pollNum}: ${msg}`);
          await page.screenshot({ path: path.join(ssDir, `stall-${pollNum}.png`), fullPage: true });
        }
      } else {
        stalledCount = 0;
      }
    }

    // Log progress
    const statusStr = Object.entries(snapshot.runStatuses)
      .filter(([, v]) => v > 0)
      .map(([k, v]) => `${v} ${k}`)
      .join(', ');

    log(`Poll ${pollNum}: ${snapshot.batchStatus} | Runs: ${statusStr} | Bars: ${snapshot.progressBarCount} | NoNew: ${snapshot.noNewCount} | Progress: ${snapshot.progressText} | Stats: ${snapshot.statsText}`);

    // Verify consistency
    verifyConsistency(snapshot, issues, pollNum);

    prevSnapshot = snapshot;

    // Done?
    if (['Completed', 'Partialfailure', 'Failed'].includes(snapshot.batchStatus)) {
      log(`Batch finished: ${snapshot.batchStatus}`);
      await page.screenshot({ path: path.join(ssDir, 'final-detail.png'), fullPage: true });

      const backBtn = page.locator('text=Back to Batches');
      if (await backBtn.count() > 0) {
        await backBtn.click();
        await page.waitForTimeout(2000);
        await page.screenshot({ path: path.join(ssDir, 'final-index.png'), fullPage: true });
      }
      break;
    }
  }

  // --- Summary ---
  console.log('\n' + '='.repeat(60));
  log('MONITORING COMPLETE');
  console.log('='.repeat(60));
  console.log(`Total polls: ${pollNum}`);
  console.log(`Duration: ~${Math.round(pollNum * POLL_INTERVAL_MS / 60000)} minutes`);
  console.log(`Final status: ${prevSnapshot?.batchStatus || 'unknown'}`);
  console.log(`Issues found: ${issues.length}`);
  if (issues.length > 0) {
    console.log('\nIssues:');
    issues.forEach((issue, i) => console.log(`  ${i + 1}. ${issue}`));
  }
  console.log('='.repeat(60));

  await app.close();
  process.exit(issues.length > 0 ? 1 : 0);
}

async function captureState(page) {
  const bodyText = await page.locator('body').innerText();
  const upper = bodyText.toUpperCase();

  // Batch status
  let batchStatus = 'unknown';
  for (const s of ['RUNNING', 'COMPLETED', 'PARTIALFAILURE', 'FAILED', 'QUEUED']) {
    if (upper.includes(s)) {
      batchStatus = s.charAt(0) + s.slice(1).toLowerCase();
      break;
    }
  }

  // Batch phase
  let batchPhase = null;
  if (upper.includes('SEARCHING')) { batchPhase = 'Searching'; }
  else if (upper.includes('PROCESSING') || upper.includes('UPDATING')) { batchPhase = 'Processing'; }

  // Count run statuses
  const runStatuses = { completed: 0, running: 0, updating: 0, searching: 0, failed: 0, queued: 0 };
  const rows = page.locator('tbody tr');
  const rowCount = await rows.count();
  for (let i = 0; i < Math.min(rowCount, 30); i++) {
    try {
      const rowText = (await rows.nth(i).innerText()).toUpperCase();
      if (rowText.includes('COMPLETED')) { runStatuses.completed++; }
      else if (rowText.includes('SEARCHED') || rowText.includes('SEARCHING')) { runStatuses.searching++; }
      else if (rowText.includes('UPDATING') || rowText.includes('PROCESSING')) { runStatuses.updating++; }
      else if (rowText.includes('RUNNING')) { runStatuses.running++; }
      else if (rowText.includes('FAILED')) { runStatuses.failed++; }
      else if (rowText.includes('QUEUED')) { runStatuses.queued++; }
    } catch { /* row disappeared mid-read */ }
  }

  // Progress bars
  const progressBars = page.locator('.progress-bar:not(.searching)');
  const progressBarCount = await progressBars.count();

  let progressText = '-';
  const progressTexts = page.locator('.progress-bar:not(.searching) .progress-text');
  if (await progressTexts.count() > 0) {
    progressText = await progressTexts.first().innerText();
  }

  // Stats banner
  let statsText = '-';
  try {
    const stats = page.locator('.stats-banner, .stat');
    if (await stats.count() > 0) {
      const allStats = await stats.allInnerTexts();
      statsText = allStats.join(' | ').replace(/\n/g, ' ').substring(0, 120);
    }
  } catch { /* stats not visible */ }

  // "No new" badges
  let noNewCount = 0;
  try {
    const noNewBadges = page.locator('.status-badge', { hasText: 'No new' });
    noNewCount = await noNewBadges.count();
  } catch { /* not visible */ }

  return {
    batchStatus,
    batchPhase,
    runStatuses,
    progressBarCount,
    progressText,
    statsText,
    noNewCount,
    totalRuns: rowCount,
  };
}

function verifyConsistency(snapshot, issues, pollNum) {
  // If batch is Running, should have at least some active runs
  if (snapshot.batchStatus === 'Running') {
    const activeRuns = snapshot.runStatuses.running + snapshot.runStatuses.searching +
      snapshot.runStatuses.updating + snapshot.runStatuses.queued;
    if (activeRuns === 0 && snapshot.runStatuses.completed + snapshot.runStatuses.failed < snapshot.totalRuns) {
      const msg = `Poll ${pollNum}: Batch RUNNING but no active runs visible`;
      logError(msg);
      issues.push(msg);
    }
  }

  // During processing, progress bars should appear for runs with new listings
  if (snapshot.batchPhase === 'Processing' && snapshot.progressBarCount === 0 && snapshot.noNewCount === 0) {
    const msg = `Poll ${pollNum}: Processing phase but no progress bars or "No new" badges`;
    logError(msg);
    issues.push(msg);
  }
}

main().catch(err => {
  logError(err.message);
  console.error(err);
  process.exit(1);
});
