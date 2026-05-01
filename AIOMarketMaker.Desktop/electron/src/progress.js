// Progress calculation functions shared between app.js and tests.
// app.js delegates to these; tests import them directly.

export function activeSoldDone(run) {
  return (run.listingsAddedActive || 0) + (run.listingsAddedSold || 0);
}

export function activeSoldTotal(run) {
  return (run.totalListingsFound || 0) - (run.listingsFilteredPreQueue || 0)
    - (run.listingsUpdated || 0) - (run.listingsSkipped || 0);
}

export function progressPercent(run) {
  const total = activeSoldTotal(run);
  const done = activeSoldDone(run);
  if (total <= 0) { return done > 0 ? 100 : 0; }
  return Math.min(100, Math.round((done / total) * 100));
}

export function searchedJobCount(batch) {
  if (batch.searchedJobCount != null) { return batch.searchedJobCount; }
  if (!batch.runs) { return 0; }
  return batch.runs.filter(r => r.totalListingsFound > 0).length;
}

export function batchDone(batch) {
  if (batch.totalListingsAddedActive != null) {
    return (batch.totalListingsAddedActive || 0) + (batch.totalListingsAddedSold || 0);
  }
  return (batch.runs || []).reduce((s, r) => s + activeSoldDone(r), 0);
}

export function batchTotal(batch) {
  if (batch.totalListingsAddedActive != null) {
    return (batch.totalListingsFound || 0) - (batch.totalListingsFilteredPreQueue || 0)
      - (batch.totalListingsUpdated || 0) - (batch.totalListingsSkipped || 0);
  }
  return (batch.runs || []).reduce((s, r) => s + activeSoldTotal(r), 0);
}

export function batchProgressPercent(batch) {
  const total = batchTotal(batch);
  const done = batchDone(batch);
  if (total <= 0) { return done > 0 ? 100 : 0; }
  return Math.min(100, Math.round((done / total) * 100));
}

export function formatDuration(ms) {
  const totalSec = Math.floor(ms / 1000);
  const h = Math.floor(totalSec / 3600);
  const m = Math.floor((totalSec % 3600) / 60);
  const s = totalSec % 60;
  if (h > 0) { return `${h}h ${m}m ${s}s`; }
  if (m > 0) { return `${m}m ${s}s`; }
  return `${s}s`;
}

export function runStats(batch, now) {
  if (!batch) { return null; }

  const batchStart = new Date(batch.startedUtc).getTime();
  const runtimeMs = now - batchStart;
  const batchPhase = batch.batchPhase || null;

  const totalFound = batch.totalListingsFound || 0;
  const filtered = batch.totalListingsFilteredPreQueue || 0;
  const skipped = batch.totalListingsSkipped || 0;
  const addedActive = batch.totalListingsAddedActive || 0;
  const addedSold = batch.totalListingsAddedSold || 0;
  const updated = batch.totalListingsUpdated || 0;
  const failed = batch.totalListingsFailed || 0;

  const done = addedActive + addedSold;
  const total = totalFound - filtered - updated - skipped;
  const remaining = Math.max(0, total - done);

  const allProcessed = addedActive + addedSold + updated + failed;
  const allToProcess = totalFound - filtered - skipped;
  const allRemaining = Math.max(0, allToProcess - allProcessed);
  const processingStart = batch.processingStartedUtc
    ? new Date(batch.processingStartedUtc).getTime()
    : 0;
  const processingSec = processingStart ? (now - processingStart) / 1000 : 0;
  const rate = processingSec > 0 ? allProcessed / processingSec : 0;
  const etaSec = rate > 0 ? allRemaining / rate : 0;

  return {
    runCount: batch.runCount || 0,
    batchPhase,
    runtimeMs,
    totalProcessed: done,
    totalToProcess: total,
    remaining: Math.max(0, remaining),
    rate,
    etaSec,
  };
}
