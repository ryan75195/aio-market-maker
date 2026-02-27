import { describe, it, expect } from 'vitest';
import {
  activeSoldDone,
  activeSoldTotal,
  progressPercent,
  searchedJobCount,
  batchDone,
  batchTotal,
  batchProgressPercent,
  formatDuration,
  runStats,
} from '../src/progress.js';

// -- Fixtures --

function makeRun(overrides = {}) {
  return {
    totalListingsFound: 0,
    listingsFilteredPreQueue: 0,
    listingsAddedActive: 0,
    listingsAddedSold: 0,
    listingsUpdated: 0,
    listingsSkipped: 0,
    listingsFailed: 0,
    ...overrides,
  };
}

function makeBatch(overrides = {}) {
  return {
    totalListingsFound: 0,
    totalListingsFilteredPreQueue: 0,
    totalListingsAddedActive: 0,
    totalListingsAddedSold: 0,
    totalListingsUpdated: 0,
    totalListingsSkipped: 0,
    totalListingsFailed: 0,
    runCount: 0,
    startedUtc: '2026-02-27T10:00:00Z',
    ...overrides,
  };
}

// A realistic completed run: 1500 found, 100 filtered, 200 updated, 1100 skipped, 95 active, 5 sold
const REALISTIC_RUN = makeRun({
  totalListingsFound: 1500,
  listingsFilteredPreQueue: 100,
  listingsAddedActive: 95,
  listingsAddedSold: 5,
  listingsUpdated: 200,
  listingsSkipped: 1100,
  listingsFailed: 0,
});

// -- Run-level tests --

describe('activeSoldDone', () => {
  it('should sum active and sold', () => {
    expect(activeSoldDone(makeRun({ listingsAddedActive: 50, listingsAddedSold: 10 }))).toBe(60);
  });

  it('should return 0 for empty run', () => {
    expect(activeSoldDone(makeRun())).toBe(0);
  });

  it('should handle null/undefined fields', () => {
    expect(activeSoldDone({})).toBe(0);
    expect(activeSoldDone({ listingsAddedActive: null })).toBe(0);
    expect(activeSoldDone({ listingsAddedActive: undefined, listingsAddedSold: 5 })).toBe(5);
  });

  it('should count only active and sold, not updated', () => {
    const run = makeRun({ listingsAddedActive: 10, listingsAddedSold: 5, listingsUpdated: 200 });
    expect(activeSoldDone(run)).toBe(15);
  });
});

describe('activeSoldTotal', () => {
  it('should equal found minus filtered, updated, and skipped', () => {
    expect(activeSoldTotal(REALISTIC_RUN)).toBe(100); // 1500 - 100 - 200 - 1100
  });

  it('should return 0 for empty run', () => {
    expect(activeSoldTotal(makeRun())).toBe(0);
  });

  it('should handle null/undefined fields', () => {
    expect(activeSoldTotal({})).toBe(0);
  });

  it('should return negative when more deducted than found', () => {
    const run = makeRun({ totalListingsFound: 10, listingsUpdated: 8, listingsSkipped: 5 });
    expect(activeSoldTotal(run)).toBe(-3);
  });

  it('should not subtract failed from total', () => {
    const run = makeRun({ totalListingsFound: 100, listingsFailed: 5 });
    expect(activeSoldTotal(run)).toBe(100);
  });

  it('should subtract all three deductions', () => {
    const run = makeRun({
      totalListingsFound: 1000,
      listingsFilteredPreQueue: 50,
      listingsUpdated: 100,
      listingsSkipped: 800,
    });
    expect(activeSoldTotal(run)).toBe(50); // 1000 - 50 - 100 - 800
  });
});

describe('progressPercent', () => {
  it('should calculate correct percentage', () => {
    expect(progressPercent(REALISTIC_RUN)).toBe(100); // 100 done / 100 total
  });

  it('should return 0 for empty run', () => {
    expect(progressPercent(makeRun())).toBe(0);
  });

  it('should return 100 when done exceeds total', () => {
    const run = makeRun({ listingsAddedActive: 10, totalListingsFound: 5 });
    expect(progressPercent(run)).toBe(100);
  });

  it('should return 100 when done > 0 but total <= 0', () => {
    const run = makeRun({ listingsAddedActive: 5, listingsSkipped: 10 });
    expect(progressPercent(run)).toBe(100);
  });

  it('should return 0 when done is 0 and total is 0', () => {
    expect(progressPercent(makeRun())).toBe(0);
  });

  it('should round to nearest integer', () => {
    // 1/3 = 33.33% -> 33
    const run = makeRun({ totalListingsFound: 3, listingsAddedActive: 1 });
    expect(progressPercent(run)).toBe(33);
  });

  it('should cap at 100', () => {
    const run = makeRun({ totalListingsFound: 10, listingsAddedActive: 15 });
    expect(progressPercent(run)).toBe(100);
  });

  it('should calculate partial progress correctly', () => {
    const run = makeRun({
      totalListingsFound: 200,
      listingsFilteredPreQueue: 20,
      listingsUpdated: 50,
      listingsSkipped: 80,
      listingsAddedActive: 25,
      listingsAddedSold: 0,
    });
    // total = 200 - 20 - 50 - 80 = 50, done = 25, percent = 50
    expect(progressPercent(run)).toBe(50);
  });
});

// -- Batch-level tests --

describe('searchedJobCount', () => {
  it('should use server value when available', () => {
    expect(searchedJobCount({ searchedJobCount: 42 })).toBe(42);
  });

  it('should use server value of 0', () => {
    expect(searchedJobCount({ searchedJobCount: 0 })).toBe(0);
  });

  it('should return 0 when no runs', () => {
    expect(searchedJobCount({})).toBe(0);
    expect(searchedJobCount({ runs: null })).toBe(0);
  });

  it('should count runs with totalListingsFound > 0', () => {
    const batch = {
      runs: [
        { totalListingsFound: 100 },
        { totalListingsFound: 0 },
        { totalListingsFound: 50 },
      ],
    };
    expect(searchedJobCount(batch)).toBe(2);
  });

  it('should return 0 when all runs have 0 found', () => {
    const batch = {
      runs: [{ totalListingsFound: 0 }, { totalListingsFound: 0 }],
    };
    expect(searchedJobCount(batch)).toBe(0);
  });
});

describe('batchDone', () => {
  it('should use server-aggregated totals when available', () => {
    const batch = makeBatch({ totalListingsAddedActive: 500, totalListingsAddedSold: 50 });
    expect(batchDone(batch)).toBe(550);
  });

  it('should sum from runs when no server totals', () => {
    const batch = {
      runs: [
        makeRun({ listingsAddedActive: 10, listingsAddedSold: 2 }),
        makeRun({ listingsAddedActive: 20, listingsAddedSold: 3 }),
      ],
    };
    expect(batchDone(batch)).toBe(35);
  });

  it('should return 0 for empty batch with no runs', () => {
    expect(batchDone({ runs: [] })).toBe(0);
    expect(batchDone({})).toBe(0);
  });

  it('should not include updated or failed in done count', () => {
    const batch = makeBatch({
      totalListingsAddedActive: 100,
      totalListingsAddedSold: 10,
      totalListingsUpdated: 500,
      totalListingsFailed: 5,
    });
    expect(batchDone(batch)).toBe(110);
  });
});

describe('batchTotal', () => {
  it('should use server-aggregated totals when available', () => {
    const batch = makeBatch({
      totalListingsFound: 5000,
      totalListingsFilteredPreQueue: 500,
      totalListingsUpdated: 2000,
      totalListingsSkipped: 2000,
    });
    expect(batchTotal(batch)).toBe(500); // 5000 - 500 - 2000 - 2000
  });

  it('should sum from runs when no server totals', () => {
    const batch = {
      runs: [
        makeRun({ totalListingsFound: 100, listingsUpdated: 50, listingsSkipped: 30 }),
        makeRun({ totalListingsFound: 200, listingsUpdated: 80, listingsSkipped: 100 }),
      ],
    };
    // run1: 100 - 0 - 50 - 30 = 20, run2: 200 - 0 - 80 - 100 = 20
    expect(batchTotal(batch)).toBe(40);
  });

  it('should return 0 for empty batch', () => {
    expect(batchTotal(makeBatch())).toBe(0);
  });

  it('should handle batch with totalListingsAddedActive = 0 (not null)', () => {
    const batch = makeBatch({ totalListingsAddedActive: 0, totalListingsFound: 100 });
    expect(batchTotal(batch)).toBe(100);
  });
});

describe('batchProgressPercent', () => {
  it('should calculate correct percentage', () => {
    const batch = makeBatch({
      totalListingsFound: 1000,
      totalListingsFilteredPreQueue: 100,
      totalListingsUpdated: 400,
      totalListingsSkipped: 400,
      totalListingsAddedActive: 50,
      totalListingsAddedSold: 0,
    });
    // total = 1000 - 100 - 400 - 400 = 100, done = 50, percent = 50
    expect(batchProgressPercent(batch)).toBe(50);
  });

  it('should return 0 for empty batch', () => {
    expect(batchProgressPercent(makeBatch())).toBe(0);
  });

  it('should return 100 when fully complete', () => {
    const batch = makeBatch({
      totalListingsFound: 100,
      totalListingsAddedActive: 100,
    });
    expect(batchProgressPercent(batch)).toBe(100);
  });

  it('should cap at 100', () => {
    const batch = makeBatch({
      totalListingsFound: 50,
      totalListingsAddedActive: 80,
    });
    expect(batchProgressPercent(batch)).toBe(100);
  });
});

// -- Consistency tests: batch methods match runStats --

describe('batch/runStats consistency', () => {
  it('should produce same totalProcessed and totalToProcess', () => {
    const batch = makeBatch({
      totalListingsFound: 10000,
      totalListingsFilteredPreQueue: 1000,
      totalListingsUpdated: 5000,
      totalListingsSkipped: 3000,
      totalListingsAddedActive: 800,
      totalListingsAddedSold: 50,
      totalListingsFailed: 10,
      runCount: 125,
      batchPhase: 'Processing',
      processingStartedUtc: '2026-02-27T10:30:00Z',
    });

    const now = new Date('2026-02-27T11:00:00Z').getTime();
    const stats = runStats(batch, now);

    expect(stats.totalProcessed).toBe(batchDone(batch));
    expect(stats.totalToProcess).toBe(batchTotal(batch));
  });

  it('should compute correct ETA based on all processed items', () => {
    const batch = makeBatch({
      totalListingsFound: 1000,
      totalListingsFilteredPreQueue: 0,
      totalListingsUpdated: 400,
      totalListingsSkipped: 0,
      totalListingsAddedActive: 100,
      totalListingsAddedSold: 0,
      totalListingsFailed: 0,
      runCount: 10,
      batchPhase: 'Processing',
      processingStartedUtc: '2026-02-27T10:00:00Z',
    });

    // 500 seconds later
    const now = new Date('2026-02-27T10:08:20Z').getTime();
    const stats = runStats(batch, now);

    // allProcessed = 100 + 0 + 400 + 0 = 500
    // allToProcess = 1000 - 0 - 0 = 1000
    // allRemaining = 500
    // rate = 500 / 500 = 1/sec
    // etaSec = 500 / 1 = 500
    expect(stats.rate).toBe(1);
    expect(stats.etaSec).toBe(500);
  });

  it('should return null when no batch', () => {
    expect(runStats(null, Date.now())).toBeNull();
  });
});

// -- formatDuration tests --

describe('formatDuration', () => {
  it('should format seconds only', () => {
    expect(formatDuration(5000)).toBe('5s');
    expect(formatDuration(0)).toBe('0s');
  });

  it('should format minutes and seconds', () => {
    expect(formatDuration(65000)).toBe('1m 5s');
    expect(formatDuration(120000)).toBe('2m 0s');
  });

  it('should format hours, minutes, and seconds', () => {
    expect(formatDuration(3661000)).toBe('1h 1m 1s');
    expect(formatDuration(7200000)).toBe('2h 0m 0s');
  });
});

// -- Edge case: realistic batch scenarios --

describe('realistic scenarios', () => {
  it('should handle a typical mid-batch state', () => {
    // 125 jobs, halfway through processing
    const batch = makeBatch({
      totalListingsFound: 147000,
      totalListingsFilteredPreQueue: 12000,
      totalListingsUpdated: 85000,
      totalListingsSkipped: 45000,
      totalListingsAddedActive: 2500,
      totalListingsAddedSold: 100,
      totalListingsFailed: 35,
      runCount: 125,
    });

    // total = 147000 - 12000 - 85000 - 45000 = 5000
    // done = 2500 + 100 = 2600
    expect(batchTotal(batch)).toBe(5000);
    expect(batchDone(batch)).toBe(2600);
    expect(batchProgressPercent(batch)).toBe(52);
  });

  it('should handle a job with zero new listings', () => {
    const run = makeRun({
      totalListingsFound: 1500,
      listingsFilteredPreQueue: 100,
      listingsUpdated: 200,
      listingsSkipped: 1200,
      listingsAddedActive: 0,
      listingsAddedSold: 0,
    });
    expect(activeSoldTotal(run)).toBe(0); // 1500 - 100 - 200 - 1200
    expect(activeSoldDone(run)).toBe(0);
    expect(progressPercent(run)).toBe(0);
  });

  it('should handle a job with only active listings', () => {
    const run = makeRun({
      totalListingsFound: 500,
      listingsFilteredPreQueue: 50,
      listingsUpdated: 100,
      listingsSkipped: 250,
      listingsAddedActive: 100,
      listingsAddedSold: 0,
    });
    expect(activeSoldTotal(run)).toBe(100); // 500 - 50 - 100 - 250
    expect(activeSoldDone(run)).toBe(100);
    expect(progressPercent(run)).toBe(100);
  });

  it('should handle a searching-phase batch', () => {
    const batch = {
      searchedJobCount: null,
      runs: [
        { totalListingsFound: 1500 },
        { totalListingsFound: 0 },
        { totalListingsFound: 800 },
        { totalListingsFound: 0 },
        { totalListingsFound: 0 },
      ],
    };
    expect(searchedJobCount(batch)).toBe(2);
  });
});
