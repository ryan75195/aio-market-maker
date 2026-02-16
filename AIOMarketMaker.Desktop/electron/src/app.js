const { createApp } = Vue;

createApp({
  data() {
    return {
      currentView: 'overview',
      config: null,
      configError: null,
      jobs: [],
      opportunities: [],
      opportunityPage: 1,
      opportunityPageSize: 50,
      opportunitySortBy: 'potentialProfit',
      opportunitySortDir: 'desc',
      opportunityJobFilter: [],
      opportunityTotalCount: 0,
      opportunityTotalPages: 0,
      historyMode: 'batches',
      batches: [],
      selectedBatch: null,
      batchPage: 1,
      batchPageSize: 20,
      batchTotalCount: 0,
      batchTotalPages: 0,
      expandedRuns: {},
      runIssues: {},
      showJobsPanel: false,
      jobSearch: '',
      jobPage: 1,
      windowHeight: window.innerHeight,
      loading: false,
      toast: null,
      showJobForm: false,
      editingJob: null,
      jobForm: {
        searchTerm: '',
        filterInstructions: '',
        isEnabled: true
      },
      // lastInstanceId removed - orchestrations replaced by inline processing
      refreshInterval: null,
      now: Date.now(),
      nowInterval: null,
      settings: {
        marketMakerApi: { baseUrl: '', functionKey: '' },
        etlApi: { baseUrl: '' },
        scraperApi: { baseUrl: '' },
        scraping: {
          maxSoldListings: 100,
          maxActiveListings: 100,
          defaultLookbackDays: 180,
          limitSoldEnabled: true,
          limitActiveEnabled: true,
          limitLookbackEnabled: false
        },
        opportunities: {
          minComps: 3,
          priceBandMultiplier: 2.0,
          feePercent: 13.25,
          matchCondition: true
        },
        storage: { connectionString: '' },
        openAi: { apiKey: '', model: '' },
        pinecone: { apiKey: '', indexName: '' }
      },
      collapsedSections: {
        api: true,
        scraping: true,
        opportunities: true,
        storage: true,
        openAi: true,
        pinecone: true,
        data: true
      },
      savingSettings: false,
      showJobDropdown: false,
      overviewData: {
        totalListings: 0,
        activeListings: 0,
        soldListings: 0,
        endedListings: 0,
        opportunities: 0,
        aggregateProfit: 0,
        lastScrape: null,
        cumulativeGrowth: [],
        listingsByJob: [],
        profitDistribution: { range0to25: 0, range25to50: 0, range50to100: 0, range100plus: 0 },
        topOpportunities: [],
        recentRuns: []
      },
      overviewCharts: {}
    };
  },

  computed: {
    jobFilterLabel() {
      if (this.opportunityJobFilter.length === 0) {
        return 'Filter: All';
      }
      if (this.opportunityJobFilter.length === 1) {
        const job = this.jobs.find(j => j.id === this.opportunityJobFilter[0]);
        return `Filter: ${job ? job.searchTerm : 'Unknown'}`;
      }
      return `Filter: ${this.opportunityJobFilter.length} jobs`;
    },

    apiTarget() {
      if (!this.config?.marketMakerApi?.baseUrl) return 'Not configured';
      const url = this.config.marketMakerApi.baseUrl;
      if (url.includes('localhost') || url.includes('127.0.0.1')) return 'Local';
      return 'Production';
    },

    opportunityPageRange() {
      const total = this.opportunityTotalPages;
      const current = this.opportunityPage;
      if (total <= 7) {
        return Array.from({ length: total }, (_, i) => i + 1);
      }
      const pages = [1];
      const start = Math.max(2, current - 1);
      const end = Math.min(total - 1, current + 1);
      if (start > 2) { pages.push('...'); }
      for (let i = start; i <= end; i++) {
        pages.push(i);
      }
      if (end < total - 1) { pages.push('...'); }
      pages.push(total);
      return pages;
    },

    historyPageRange() {
      const total = this.batchTotalPages;
      const current = this.batchPage;
      if (total <= 7) {
        return Array.from({ length: total }, (_, i) => i + 1);
      }
      const pages = [1];
      const start = Math.max(2, current - 1);
      const end = Math.min(total - 1, current + 1);
      if (start > 2) { pages.push('...'); }
      for (let i = start; i <= end; i++) {
        pages.push(i);
      }
      if (end < total - 1) { pages.push('...'); }
      pages.push(total);
      return pages;
    },

    filteredJobs() {
      let result = this.jobs;
      if (this.jobSearch) {
        const q = this.jobSearch.toLowerCase();
        result = result.filter(j =>
          j.searchTerm?.toLowerCase().includes(q) ||
          j.filterInstructions?.toLowerCase().includes(q)
        );
      }
      return [...result].sort((a, b) => {
        const aTime = a.lastRunUtc ? new Date(a.lastRunUtc).getTime() : 0;
        const bTime = b.lastRunUtc ? new Date(b.lastRunUtc).getTime() : 0;
        return bTime - aTime;
      });
    },

    jobPageSize() {
      // ~37px per row, reserve ~200px for header/toolbar/pagination
      return Math.max(5, Math.floor((this.windowHeight - 200) / 37));
    },

    jobTotalPages() {
      return Math.ceil(this.filteredJobs.length / this.jobPageSize);
    },

    paginatedJobs() {
      const start = (this.jobPage - 1) * this.jobPageSize;
      return this.filteredJobs.slice(start, start + this.jobPageSize);
    },

    jobPageRange() {
      const total = this.jobTotalPages;
      const current = this.jobPage;
      if (total <= 7) {
        return Array.from({ length: total }, (_, i) => i + 1);
      }
      const pages = [1];
      const start = Math.max(2, current - 1);
      const end = Math.min(total - 1, current + 1);
      if (start > 2) { pages.push('...'); }
      for (let i = start; i <= end; i++) {
        pages.push(i);
      }
      if (end < total - 1) { pages.push('...'); }
      pages.push(total);
      return pages;
    },

    activeRuns() {
      const statuses = ['Queued', 'Running', 'Indexing', 'Searching', 'Processing'];
      const allRuns = this.batches.flatMap(b => b.runs || []);
      return allRuns.filter(r => statuses.includes(r.status));
    },

    runStats() {
      const active = this.activeRuns;
      if (active.length === 0) { return null; }

      const earliest = Math.min(...active.map(r => new Date(r.startedUtc).getTime()));
      const runtimeMs = this.now - earliest;

      // Include completed runs from same batch for stable rate calculation
      const allRuns = this.batches.flatMap(b => b.runs || []);
      const batchRuns = allRuns.filter(r => new Date(r.startedUtc).getTime() >= earliest);

      // Split into runs with data vs queued (no listing data yet)
      const startedRuns = batchRuns.filter(r => r.status !== 'Queued');
      const queuedRuns = batchRuns.filter(r => r.status === 'Queued');

      // Extrapolate queued runs using average from started runs
      const avgToProcess = startedRuns.length > 0
        ? startedRuns.reduce((s, r) => s + this.listingsToProcess(r), 0) / startedRuns.length
        : 0;

      const batchProcessed = startedRuns.reduce((s, r) => s + (r.listingsProcessed || 0), 0);
      const batchToProcess = startedRuns.reduce((s, r) => s + this.listingsToProcess(r), 0)
        + Math.round(avgToProcess * queuedRuns.length);

      // Remaining: active non-queued remaining + extrapolated queued
      const activeStarted = active.filter(r => r.status !== 'Queued');
      const activeQueued = active.filter(r => r.status === 'Queued');
      const activeRemaining = activeStarted.reduce((s, r) => s + this.listingsToProcess(r) - (r.listingsProcessed || 0), 0)
        + Math.round(avgToProcess * activeQueued.length);

      const runtimeSec = runtimeMs / 1000;
      const rate = runtimeSec > 0 ? batchProcessed / runtimeSec : 0;
      const etaSec = rate > 0 ? activeRemaining / rate : 0;

      return {
        activeCount: active.length,
        queuedCount: activeQueued.length,
        runtimeMs,
        totalProcessed: batchProcessed,
        totalToProcess: batchToProcess,
        remaining: activeRemaining,
        rate,
        etaSec,
      };
    }
  },

  async mounted() {
    this._onResize = () => { this.windowHeight = window.innerHeight; };
    window.addEventListener('resize', this._onResize);
    await this.loadConfig();
    if (!this.configError) {
      await this.loadJobs();
      await this.loadOverview();
    }
    this.startAutoRefresh();
  },

  beforeUnmount() {
    window.removeEventListener('resize', this._onResize);
    this.stopAutoRefresh();
  },

  methods: {
    async loadConfig() {
      try {
        const result = await window.electronAPI.getConfig();
        if (result.error) {
          this.configError = result.error;
          this.showToast(result.error, 'error');
        } else {
          this.config = result;
          // Populate settings from config (deep copy)
          this.settings = JSON.parse(JSON.stringify(result));
        }
      } catch (err) {
        this.configError = err.message;
        this.showToast('Failed to load config', 'error');
      }
    },

    async loadOverview() {
      try {
        const opp = this.settings?.opportunities || {};
        const params = new URLSearchParams();
        if (opp.minComps > 0) {
          params.set('minComps', opp.minComps);
        }
        if (opp.feePercent > 0) {
          params.set('feePercent', opp.feePercent);
        }
        if (opp.matchCondition) {
          params.set('matchCondition', 'true');
        }
        const data = await this.apiCall(`/overview?${params.toString()}`);
        this.overviewData = this.toCamelCase(data);
        this.$nextTick(() => this.renderCharts());
      } catch (err) {
        this.showToast(`Failed to load overview: ${err.message}`, 'error');
      }
    },

    renderCharts() {
      this.renderCumulativeGrowthChart();
      this.renderListingsByJobChart();
      this.renderProfitDistributionChart();
    },

    renderCumulativeGrowthChart() {
      const canvas = document.getElementById('cumulativeGrowthChart');
      if (!canvas) { return; }

      if (this.overviewCharts.cumulativeGrowth) {
        this.overviewCharts.cumulativeGrowth.destroy();
      }

      const data = this.overviewData.cumulativeGrowth || [];
      this.overviewCharts.cumulativeGrowth = new Chart(canvas, {
        type: 'line',
        data: {
          labels: data.map(d => d.date),
          datasets: [{
            label: 'Total Listings',
            data: data.map(d => d.cumulative),
            borderColor: '#4a9eff',
            backgroundColor: 'rgba(74, 158, 255, 0.1)',
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

    renderListingsByJobChart() {
      const canvas = document.getElementById('listingsByJobChart');
      if (!canvas) { return; }

      if (this.overviewCharts.listingsByJob) {
        this.overviewCharts.listingsByJob.destroy();
      }

      const data = this.overviewData.listingsByJob || [];
      const colors = ['#4a9eff', '#22c55e', '#f59e0b', '#ef4444', '#a855f7', '#06b6d4', '#ec4899', '#84cc16'];
      this.overviewCharts.listingsByJob = new Chart(canvas, {
        type: 'bar',
        data: {
          labels: data.map(d => d.searchTerm),
          datasets: [{
            data: data.map(d => d.count),
            backgroundColor: data.map((_, i) => colors[i % colors.length]),
            borderRadius: 4
          }]
        },
        options: {
          indexAxis: 'y',
          responsive: true,
          maintainAspectRatio: false,
          plugins: {
            legend: { display: false }
          },
          scales: {
            x: {
              ticks: { color: '#808080' },
              grid: { color: '#3c3c3c' },
              beginAtZero: true
            },
            y: {
              ticks: { color: '#e0e0e0' },
              grid: { display: false }
            }
          }
        }
      });
    },

    renderProfitDistributionChart() {
      const canvas = document.getElementById('profitDistributionChart');
      if (!canvas) { return; }

      if (this.overviewCharts.profitDistribution) {
        this.overviewCharts.profitDistribution.destroy();
      }

      const dist = this.overviewData.profitDistribution || {};
      this.overviewCharts.profitDistribution = new Chart(canvas, {
        type: 'bar',
        data: {
          labels: ['$0-25', '$25-50', '$50-100', '$100+'],
          datasets: [{
            data: [dist.range0to25 || 0, dist.range25to50 || 0, dist.range50to100 || 0, dist.range100plus || 0],
            backgroundColor: ['#4a9eff', '#22c55e', '#f59e0b', '#ef4444'],
            borderRadius: 4
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
              ticks: { color: '#e0e0e0' },
              grid: { display: false }
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

    timeAgo(dateStr) {
      if (!dateStr) { return 'Never'; }
      const diff = this.now - new Date(dateStr).getTime();
      const minutes = Math.floor(diff / 60000);
      if (minutes < 1) { return 'Just now'; }
      if (minutes < 60) { return `${minutes}m ago`; }
      const hours = Math.floor(minutes / 60);
      if (hours < 24) { return `${hours}h ago`; }
      const days = Math.floor(hours / 24);
      return `${days}d ago`;
    },

    async apiCall(endpoint, options = {}) {
      const baseUrl = this.config.marketMakerApi.baseUrl;
      const functionKey = this.config.marketMakerApi.functionKey;

      const url = new URL(`${baseUrl}${endpoint}`);
      if (functionKey && functionKey !== 'your-function-key-here') {
        url.searchParams.set('code', functionKey);
      }

      const response = await fetch(url.toString(), {
        ...options,
        headers: {
          'Content-Type': 'application/json',
          ...options.headers
        }
      });

      if (!response.ok) {
        const error = await response.json().catch(() => ({ error: response.statusText }));
        throw new Error(error.error || `HTTP ${response.status}`);
      }

      return response.json();
    },

    async etlApiCall(endpoint, options = {}) {
      const baseUrl = this.config.etlApi?.baseUrl || 'http://localhost:7072/api';
      const timeout = options.timeout || 30000; // Default 30s, can be overridden

      const url = new URL(`${baseUrl}${endpoint}`);

      const controller = new AbortController();
      const timeoutId = setTimeout(() => controller.abort(), timeout);

      try {
        const response = await fetch(url.toString(), {
          ...options,
          signal: controller.signal,
          headers: {
            'Content-Type': 'application/json',
            ...options.headers
          }
        });

        if (!response.ok) {
          const error = await response.json().catch(() => ({ error: response.statusText }));
          throw new Error(error.error || `HTTP ${response.status}`);
        }

        return response.json();
      } finally {
        clearTimeout(timeoutId);
      }
    },

    // Convert PascalCase keys from .NET API to camelCase for JavaScript
    toCamelCase(obj) {
      if (Array.isArray(obj)) {
        return obj.map(item => this.toCamelCase(item));
      }
      if (obj !== null && typeof obj === 'object') {
        return Object.keys(obj).reduce((result, key) => {
          const camelKey = key.charAt(0).toLowerCase() + key.slice(1);
          result[camelKey] = this.toCamelCase(obj[key]);
          return result;
        }, {});
      }
      return obj;
    },

    async loadJobs() {
      try {
        const data = await this.apiCall('/jobs');
        this.jobs = this.toCamelCase(data);
      } catch (err) {
        this.showToast(`Failed to load jobs: ${err.message}`, 'error');
      }
    },

    async loadHistory() {
      try {
        const params = new URLSearchParams({
          page: this.batchPage,
          pageSize: this.batchPageSize
        });
        const data = await this.apiCall(`/history/batches?${params}`);
        const result = this.toCamelCase(data);
        this.batches = result.items || [];
        this.batchTotalCount = result.totalCount || 0;
        this.batchTotalPages = result.totalPages || 0;
      } catch (err) {
        this.showToast(`Failed to load history: ${err.message}`, 'error');
      }
    },

    selectBatch(batch) {
      this.selectedBatch = batch;
      this.historyMode = 'runs';
      this.expandedRuns = {};
      this.runIssues = {};
    },

    backToBatches() {
      this.historyMode = 'batches';
      this.selectedBatch = null;
      this.expandedRuns = {};
    },

    goToHistoryPage(page) {
      if (page < 1 || page > this.batchTotalPages) { return; }
      this.batchPage = page;
      this.loadHistory();
    },

    async loadOpportunities() {
      try {
        const opp = this.settings?.opportunities || this.config?.opportunities || {};
        const params = new URLSearchParams({
          page: this.opportunityPage,
          pageSize: this.opportunityPageSize,
          sortBy: this.opportunitySortBy,
          sortDir: this.opportunitySortDir
        });
        if (this.opportunityJobFilter.length > 0) {
          params.set('jobIds', this.opportunityJobFilter.join(','));
        }
        if (opp.minComps > 0) {
          params.set('minComps', opp.minComps);
        }
        if (opp.priceBandMultiplier > 0) {
          params.set('priceBand', opp.priceBandMultiplier);
        }
        if (opp.feePercent > 0) {
          params.set('feePercent', opp.feePercent);
        }
        if (opp.matchCondition) {
          params.set('matchCondition', 'true');
        }
        const data = await this.apiCall(`/listings/active?${params.toString()}`);
        const result = this.toCamelCase(data);
        this.opportunities = result.items;
        this.opportunityTotalCount = result.totalCount;
        this.opportunityTotalPages = result.totalPages;
      } catch (err) {
        this.showToast(`Failed to load opportunities: ${err.message}`, 'error');
      }
    },

    sortOpportunities(column) {
      if (this.opportunitySortBy === column) {
        this.opportunitySortDir = this.opportunitySortDir === 'asc' ? 'desc' : 'asc';
      } else {
        this.opportunitySortBy = column;
        this.opportunitySortDir = 'desc';
      }
      this.opportunityPage = 1;
      this.loadOpportunities();
    },

    sortIndicator(column) {
      if (this.opportunitySortBy !== column) { return ''; }
      return this.opportunitySortDir === 'asc' ? ' \u25B2' : ' \u25BC';
    },

    isJobFiltered(jobId) {
      return this.opportunityJobFilter.includes(jobId);
    },

    toggleJobFilter(jobId) {
      const idx = this.opportunityJobFilter.indexOf(jobId);
      if (idx >= 0) {
        this.opportunityJobFilter.splice(idx, 1);
      } else {
        this.opportunityJobFilter.push(jobId);
      }
      this.opportunityPage = 1;
      this.loadOpportunities();
    },

    clearJobFilter() {
      this.opportunityJobFilter = [];
      this.opportunityPage = 1;
      this.loadOpportunities();
    },

    goToPage(page) {
      if (page < 1 || page > this.opportunityTotalPages) { return; }
      this.opportunityPage = page;
      this.loadOpportunities();
    },

    getFirstImage(imagesJson) {
      if (!imagesJson) return null;
      try {
        const images = JSON.parse(imagesJson);
        return Array.isArray(images) && images.length > 0 ? images[0] : null;
      } catch {
        return null;
      }
    },

    truncate(text, maxLength) {
      if (!text) return '-';
      return text.length > maxLength ? text.substring(0, maxLength) + '...' : text;
    },

    formatPrice(price, currency) {
      if (price == null) return '-';
      const symbol = currency === 'GBP' ? '\u00A3' : currency === 'USD' ? '$' : (currency || '');
      return `${symbol}${price.toLocaleString(undefined, { minimumFractionDigits: 2, maximumFractionDigits: 2 })}`;
    },

    async toggleJob(job) {
      try {
        const endpoint = job.isEnabled ? `/jobs/${job.id}/disable` : `/jobs/${job.id}/enable`;
        await this.apiCall(endpoint, { method: 'POST' });
        job.isEnabled = !job.isEnabled;
        this.showToast(`Job ${job.isEnabled ? 'enabled' : 'disabled'}`, 'success');
      } catch (err) {
        this.showToast(`Failed to toggle job: ${err.message}`, 'error');
      }
    },

    editJob(job) {
      this.editingJob = job;
      this.jobForm = {
        searchTerm: job.searchTerm,
        filterInstructions: job.filterInstructions || '',
        isEnabled: job.isEnabled
      };
      this.showJobForm = true;
    },

    async deleteJob(job) {
      if (!confirm(`Delete job "${job.searchTerm}"?`)) return;

      try {
        await this.apiCall(`/jobs/${job.id}`, { method: 'DELETE' });
        this.jobs = this.jobs.filter(j => j.id !== job.id);
        this.showToast('Job deleted', 'success');
      } catch (err) {
        this.showToast(`Failed to delete job: ${err.message}`, 'error');
      }
    },

    async saveJob() {
      try {
        if (this.editingJob) {
          const data = await this.apiCall(`/jobs/${this.editingJob.id}`, {
            method: 'PUT',
            body: JSON.stringify(this.jobForm)
          });
          const updated = this.toCamelCase(data);
          const index = this.jobs.findIndex(j => j.id === this.editingJob.id);
          if (index !== -1) this.jobs[index] = updated;
          this.showToast('Job updated', 'success');
        } else {
          const data = await this.apiCall('/jobs', {
            method: 'POST',
            body: JSON.stringify(this.jobForm)
          });
          const created = this.toCamelCase(data);
          this.jobs.push(created);
          this.showToast('Job created', 'success');
        }
        this.closeJobForm();
      } catch (err) {
        this.showToast(`Failed to save job: ${err.message}`, 'error');
      }
    },

    closeJobForm() {
      this.showJobForm = false;
      this.editingJob = null;
      this.jobForm = { searchTerm: '', filterInstructions: '', isEnabled: true };
    },

    async startScrape() {
      this.loading = true;
      try {
        const data = await this.apiCall('/scrape/start', {
          method: 'POST',
          timeout: 120000
        });
        const result = this.toCamelCase(data);
        const runCount = result.runs?.length || 0;
        this.showToast(`Scrape started (${runCount} job${runCount !== 1 ? 's' : ''})`, 'success');

        // Switch to history view to see progress
        this.historyMode = 'batches';
        this.currentView = 'index';
        await this.loadHistory();
      } catch (err) {
        this.showToast(`Failed to start scrape: ${err.message}`, 'error');
      } finally {
        this.loading = false;
      }
    },

    async clearAllData() {
      if (!confirm('Delete ALL scrape data (listings, run history, vector index, and tracking data)? This cannot be undone.')) return;

      this.loading = true;
      try {
        const data = await this.apiCall('/data/all', { method: 'DELETE' });
        const result = this.toCamelCase(data);
        const indexMsg = result.indexCleared ? ', vector index cleared' : '';
        this.showToast(`Cleared ${result.deletedListings} listings and ${result.deletedRuns} history records${indexMsg}`, 'success');
        if (this.currentView === 'index') {
          await this.loadHistory();
        } else if (this.currentView === 'opportunities') {
          await this.loadOpportunities();
        }
      } catch (err) {
        this.showToast(`Failed to clear data: ${err.message}`, 'error');
      } finally {
        this.loading = false;
      }
    },

    async clearListings() {
      if (!confirm('Delete ALL listings from the database? This cannot be undone.')) return;

      this.loading = true;
      try {
        const data = await this.apiCall('/listings/all', { method: 'DELETE' });
        const result = this.toCamelCase(data);
        const indexMsg = result.indexCleared ? ', vector index cleared' : '';
        this.showToast(`Cleared ${result.deleted} listings${indexMsg}`, 'success');
        // Refresh opportunities if on that view
        if (this.currentView === 'opportunities') {
          await this.loadOpportunities();
        }
      } catch (err) {
        this.showToast(`Failed to clear listings: ${err.message}`, 'error');
      } finally {
        this.loading = false;
      }
    },

    async clearHistory() {
      if (!confirm('Delete ALL run history from the database? This cannot be undone.')) return;

      this.loading = true;
      try {
        const data = await this.apiCall('/history/all', { method: 'DELETE' });
        const result = this.toCamelCase(data);
        this.showToast(`Cleared ${result.deleted} history records`, 'success');
        // Refresh history if on that view
        if (this.currentView === 'index') {
          await this.loadHistory();
        }
      } catch (err) {
        this.showToast(`Failed to clear history: ${err.message}`, 'error');
      } finally {
        this.loading = false;
      }
    },

    formatDate(dateStr) {
      if (!dateStr) return 'Never';
      const date = new Date(dateStr);
      return date.toLocaleString();
    },

    listingsToProcess(run) {
      // Actual listings to process = total found - pre-queue filtered (terminal status)
      return (run.totalListingsFound || 0) - (run.listingsFilteredPreQueue || 0);
    },

    progressPercent(run) {
      const toProcess = this.listingsToProcess(run);
      if (toProcess <= 0) return 0;
      return Math.round((run.listingsProcessed / toProcess) * 100);
    },

    formatDuration(ms) {
      const totalSec = Math.floor(ms / 1000);
      const h = Math.floor(totalSec / 3600);
      const m = Math.floor((totalSec % 3600) / 60);
      const s = totalSec % 60;
      if (h > 0) { return `${h}h ${m}m ${s}s`; }
      if (m > 0) { return `${m}m ${s}s`; }
      return `${s}s`;
    },

    startAutoRefresh() {
      this.nowInterval = setInterval(() => { this.now = Date.now(); }, 1000);
      this.refreshInterval = setInterval(() => {
        if (this.currentView === 'index') {
          const activeStatuses = ['Queued', 'Running', 'Indexing', 'Searching', 'Processing'];
          const hasActive = this.batches.some(b =>
            ['Running', 'Queued'].includes(b.status));
          if (hasActive) {
            this.loadHistory();
            // Also refresh issues for any expanded active runs in the selected batch
            if (this.selectedBatch) {
              (this.selectedBatch.runs || [])
                .filter(r => activeStatuses.includes(r.status) && this.expandedRuns[r.id])
                .forEach(r => this.loadRunIssues(r.id));
            }
          }
        }
      }, 2000);
    },

    stopAutoRefresh() {
      if (this.nowInterval) {
        clearInterval(this.nowInterval);
        this.nowInterval = null;
      }
      if (this.refreshInterval) {
        clearInterval(this.refreshInterval);
        this.refreshInterval = null;
      }
    },

    showToast(message, type = 'info') {
      this.toast = { message, type };
      setTimeout(() => {
        this.toast = null;
      }, 4000);
    },

    toggleSection(section) {
      this.collapsedSections[section] = !this.collapsedSections[section];
    },

    async saveSettings() {
      this.savingSettings = true;
      try {
        const result = await window.electronAPI.saveConfig(this.settings);
        if (result.error) {
          this.showToast(`Failed to save: ${result.error}`, 'error');
        } else {
          this.config = JSON.parse(JSON.stringify(this.settings)); // Update active config
          this.showToast('Settings saved', 'success');
        }
      } catch (err) {
        this.showToast(`Failed to save: ${err.message}`, 'error');
      } finally {
        this.savingSettings = false;
      }
    },

    async toggleRunExpanded(run) {
      if (!run.listingsFailed || run.listingsFailed === 0) return;

      const runId = run.id;
      if (this.expandedRuns[runId]) {
        // Collapse
        this.expandedRuns[runId] = false;
      } else {
        // Expand and fetch issues if not already loaded
        this.expandedRuns[runId] = true;
        if (!this.runIssues[runId]) {
          await this.loadRunIssues(runId);
        }
      }
    },

    async loadRunIssues(runId) {
      try {
        const data = await this.apiCall(`/history/${runId}/issues`);
        this.runIssues[runId] = this.toCamelCase(data);
      } catch (err) {
        this.showToast(`Failed to load issues: ${err.message}`, 'error');
        this.runIssues[runId] = [];
      }
    },

    formatIssueType(issueType) {
      if (!issueType) return 'Unknown issue';
      // Convert SCREAMING_SNAKE_CASE to "Sentence case"
      return issueType
        .toLowerCase()
        .replace(/_/g, ' ')
        .replace(/^\w/, c => c.toUpperCase());
    },

    getEbayListingUrl(listingId) {
      return `https://www.ebay.co.uk/itm/${listingId}`;
    }
  }
}).mount('#app');
