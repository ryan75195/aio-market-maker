const { createApp } = Vue;

createApp({
  data() {
    return {
      currentView: 'overview',
      config: null,
      configError: null,
      overviewLoading: false,
      overviewError: null,
      opportunitiesLoading: false,
      historyLoading: false,
      jobs: [],
      opportunities: [],
      opportunityPage: 1,
      opportunitySortBy: 'potentialProfit',
      opportunitySortDir: 'desc',
      opportunityJobFilter: [],
      opportunityCategoryFilter: [],
      opportunityBudget: null,
      opportunitySearchQuery: '',
      opportunitySearchLoading: false,
      opportunityTotalCount: 0,
      opportunityTotalPages: 0,
      // Listing detail view
      selectedListingId: null,
      listingDetail: null,
      listingDetailLoading: false,
      descriptionExpanded: false,
      compareComp: null,
      compareIndex: -1,
      // Shared confirm dialog
      confirmDialog: null, // { title, message, confirmLabel, resolve }
      historyMode: 'batches',
      batches: [],
      selectedBatch: null,
      batchPage: 1,
      compPage: 1,
      batchTotalCount: 0,
      batchTotalPages: 0,
      expandedRuns: {},
      runIssues: {},
      showJobsPanel: false,
      jobOverviewMode: 'jobs',
      categories: [],
      expandedCategories: {},
      newCategoryName: '',
      showNewCategoryForm: false,
      categorySearch: '',
      jobSearch: '',
      jobPage: 1,
      jobSortBy: 'id',
      jobSortDir: 'desc',
      windowHeight: window.innerHeight,
      loading: false,
      toast: null,
      showJobForm: false,
      editingJob: null,
      jobForm: {
        searchTerm: '',
        filterInstructions: '',
        isEnabled: true,
        categoryIds: []
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
      showCategoryDropdown: false,
      overviewData: {
        totalListings: 0,
        activeListings: 0,
        soldListings: 0,
        endedListings: 0,
        opportunities: 0,
        aggregateProfit: 0,
        lastScrape: null,
        cumulativeGrowth: [],
        topJobsByOpportunities: [],
        avgProfitByCondition: [],
        avgDaysToSellByJob: [],
        priceVsProfitPoints: [],
        topOpportunities: [],
        recentRuns: []
      },
      overviewCharts: {},
      // Markets tab state
      marketsLoading: false,
      marketsError: null,
      marketsData: null,
      marketsSearch: '',
      marketsGroupFilter: '',
      marketsSortKey: 'salesPerDay',
      marketsSortDir: -1,
      // Markets detail (drill-in)
      marketsSelected: null,
      marketsListings: [],
      marketsListingsLoading: false,
      marketsListingSearch: '',
      marketsStatusFilter: '',
      marketsConditionFilter: '',
      marketsMinPrice: '',
      marketsMaxPrice: '',
      marketsMinDays: '',
      marketsMaxDays: '',
      marketsRegex: '',
      marketsRegexError: '',
      marketsShowFilters: false,
      // Chat panel
      marketsChatOpen: false,
      marketsChatMessages: [],
      marketsChatInput: '',
      marketsChatLoading: false,
      marketsListingSortKey: 'daysOnMarket',
      marketsListingSortDir: 1,
      marketsListingPage: 1,
      marketsListingPageSize: 50,
      marketsListingTotal: 0,
      marketsListingStats: null,
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

    categoryFilterLabel() {
      if (this.opportunityCategoryFilter.length === 0) {
        return 'Category: All';
      }
      if (this.opportunityCategoryFilter.length === 1) {
        const id = this.opportunityCategoryFilter[0];
        if (id === -1) { return 'Category: Uncategorised'; }
        const cat = this.categories.find(c => c.id === id);
        return `Category: ${cat ? cat.name : 'Unknown'}`;
      }
      return `Category: ${this.opportunityCategoryFilter.length} selected`;
    },

    opportunityPageSize() {
      // ~36px per row (8px padding*2 + 13px font*1.5 line-height), ~220px overhead
      return Math.max(5, Math.floor((this.windowHeight - 220) / 36));
    },

    batchPageSize() {
      // ~36px per row, ~240px overhead (header/stats/table-head/pagination/padding)
      return Math.max(5, Math.floor((this.windowHeight - 240) / 36));
    },

    sortedBatchRuns() {
      if (!this.selectedBatch || !this.selectedBatch.runs) { return []; }
      const runs = [...this.selectedBatch.runs];
      if (this.selectedBatch.batchPhase === 'Searching') {
        const priority = (r) => {
          if (r.status === 'Searching' && !r.totalListingsFound) { return 0; } // actively searching
          if (r.totalListingsFound > 0) { return 1; } // search done
          return 2; // waiting
        };
        runs.sort((a, b) => priority(a) - priority(b));
      } else {
        // Processing phase: active runs first, then completed, then queued
        const priority = (r) => {
          if (r.status === 'Running') { return 0; }
          if (r.status === 'Failed') { return 1; }
          if (r.status === 'Completed') { return 2; }
          return 3; // Queued
        };
        runs.sort((a, b) => priority(a) - priority(b));
      }
      return runs;
    },

    compPageSize() {
      // ~220px per comp card row, reserve ~380px for header/anchor/comps-header/pagination
      const available = this.windowHeight - 380;
      const cardsPerRow = 3;
      const rows = Math.max(1, Math.floor(available / 220));
      return cardsPerRow * rows;
    },

    paginatedComps() {
      if (!this.listingDetail || !this.listingDetail.comparables) { return []; }
      const start = (this.compPage - 1) * this.compPageSize;
      return this.listingDetail.comparables.slice(start, start + this.compPageSize);
    },

    compTotalPages() {
      if (!this.listingDetail || !this.listingDetail.comparables) { return 0; }
      return Math.ceil(this.listingDetail.comparables.length / this.compPageSize);
    },

    compPageRange() {
      const total = this.compTotalPages;
      const current = this.compPage;
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

    filteredJobIds() {
      if (this.opportunityCategoryFilter.length === 0) {
        return null;
      }
      const catIds = this.opportunityCategoryFilter;
      const matchingJobs = this.jobs.filter(j => {
        const jobCats = j.categories || [];
        if (catIds.includes(-1) && jobCats.length === 0) { return true; }
        return jobCats.some(c => catIds.includes(c.id));
      });
      return matchingJobs.map(j => j.id);
    },

    categoryFilteredJobs() {
      if (this.opportunityCategoryFilter.length === 0) {
        return this.jobs;
      }
      const ids = this.filteredJobIds;
      if (!ids) { return this.jobs; }
      return this.jobs.filter(j => ids.includes(j.id));
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
      const dir = this.jobSortDir === 'asc' ? 1 : -1;
      const key = this.jobSortBy;
      return [...result].sort((a, b) => {
        let aVal = a[key];
        let bVal = b[key];
        if (key === 'lastRunUtc') {
          aVal = aVal ? new Date(aVal).getTime() : 0;
          bVal = bVal ? new Date(bVal).getTime() : 0;
        } else if (key === 'searchTerm') {
          aVal = (aVal || '').toLowerCase();
          bVal = (bVal || '').toLowerCase();
          return aVal < bVal ? -dir : aVal > bVal ? dir : 0;
        } else if (key === 'isEnabled') {
          aVal = aVal ? 1 : 0;
          bVal = bVal ? 1 : 0;
        }
        return (aVal - bVal) * dir;
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

    filteredCategories() {
      if (!this.categorySearch) { return this.categories; }
      const q = this.categorySearch.toLowerCase();
      return this.categories.filter(c => c.name.toLowerCase().includes(q));
    },

    activeRuns() {
      const statuses = ['Queued', 'Running', 'Indexing', 'Searching', 'Processing'];
      const allRuns = this.batches.flatMap(b => b.runs || []);
      return allRuns.filter(r => statuses.includes(r.status));
    },

    detailStats() {
      if (!this.selectedBatch || !['Running', 'Queued'].includes(this.selectedBatch.status)) { return null; }
      // The detail endpoint doesn't have aggregated listing fields —
      // compute them from individual runs
      const batch = this.selectedBatch;
      const runs = batch.runs || [];
      const agg = {
        ...batch,
        totalListingsAddedActive: runs.reduce((s, r) => s + (r.listingsAddedActive || 0), 0),
        totalListingsAddedSold: runs.reduce((s, r) => s + (r.listingsAddedSold || 0), 0),
        totalListingsUpdated: runs.reduce((s, r) => s + (r.listingsUpdated || 0), 0),
        totalListingsSkipped: runs.reduce((s, r) => s + (r.listingsSkipped || 0), 0),
        totalListingsFailed: runs.reduce((s, r) => s + (r.listingsFailed || 0), 0),
        totalListingsFilteredPreQueue: runs.reduce((s, r) => s + (r.listingsFilteredPreQueue || 0), 0),
      };
      return this._batchStats(agg);
    },

    runStats() {
      const activeBatch = this.batches.find(b => ['Running', 'Queued'].includes(b.status));
      if (!activeBatch) { return null; }
      return this._batchStats(activeBatch);
    },

    // ── Markets tab computed ─────────────────────────────────

    filteredMarkets() {
      if (!this.marketsData?.jobs) { return []; }
      let list = this.marketsData.jobs;
      if (this.marketsSearch) {
        const q = this.marketsSearch.toLowerCase();
        list = list.filter(j => j.searchTerm?.toLowerCase().includes(q));
      }
      if (this.marketsGroupFilter) {
        list = list.filter(j => j.categories?.includes(this.marketsGroupFilter));
      }
      const key = this.marketsSortKey;
      const dir = this.marketsSortDir;
      if (key === 'searchTerm') {
        return [...list].sort((a, b) => (a.searchTerm || '').localeCompare(b.searchTerm || '') * dir);
      }
      return [...list].sort((a, b) => ((a[key] ?? 0) - (b[key] ?? 0)) * dir);
    },

    marketsMaxVelocity() {
      if (!this.marketsData?.jobs) { return 1; }
      return Math.max(...this.marketsData.jobs.map(j => j.salesPerDay), 1);
    },

    marketsTotalSold() {
      if (!this.marketsData?.jobs) { return 0; }
      return this.marketsData.jobs.reduce((s, j) => s + j.soldCount, 0);
    },

    marketsTotalSalesPerDay() {
      if (!this.marketsData?.jobs) { return 0; }
      return this.marketsData.jobs.reduce((s, j) => s + j.salesPerDay, 0);
    },

    marketsActiveFilterCount() {
      let count = 0;
      if (this.marketsListingSearch) { count++; }
      if (this.marketsStatusFilter) { count++; }
      if (this.marketsConditionFilter) { count++; }
      if (this.marketsMinPrice) { count++; }
      if (this.marketsMaxPrice) { count++; }
      if (this.marketsMinDays) { count++; }
      if (this.marketsMaxDays) { count++; }
      if (this.marketsRegex) { count++; }
      return count;
    },

    marketsDetailKpis() {
      const s = this.marketsListingStats;
      if (!s) { return { count: 0, sold: 0, active: 0, sellThrough: 0, avgDays: 0, avgPrice: '0', priceRange: '0' }; }
      const avgPrice = s.avgPrice ? s.avgPrice.toFixed(0) : '0';
      const priceRange = s.minPrice && s.maxPrice
        ? s.minPrice.toFixed(0) + '\u2013' + s.maxPrice.toFixed(0)
        : '0';
      return {
        count: this.marketsListingTotal,
        sold: s.soldCount,
        active: s.activeCount,
        sellThrough: s.sellThrough,
        avgDays: s.avgDaysToSell,
        avgPrice,
        priceRange,
      };
    },
  },

  watch: {
    marketsListingSearch() {
      this._debouncedMarketsReload();
    },
    marketsConditionFilter() {
      this._debouncedMarketsReload();
    },
    marketsMinPrice() {
      this._debouncedMarketsReload();
    },
    marketsMaxPrice() {
      this._debouncedMarketsReload();
    },
    marketsMinDays() {
      this._debouncedMarketsReload();
    },
    marketsMaxDays() {
      this._debouncedMarketsReload();
    },
    marketsRegex() {
      this._debouncedMarketsReload();
    },
  },

  async mounted() {
    this._onKeydown = (e) => {
      if (e.key === 'Escape' && this.confirmDialog) {
        this.resolveConfirm(false);
        return;
      }
      if (!this.compareComp) { return; }
      if (e.key === 'Escape') { this.closeCompare(); }
      if (e.key === 'ArrowLeft') { this.compareNav(-1); }
      if (e.key === 'ArrowRight') { this.compareNav(1); }
    };
    window.addEventListener('keydown', this._onKeydown);
    this._onResize = () => {
      this.windowHeight = window.innerHeight;
      clearTimeout(this._resizeTimer);
      this._resizeTimer = setTimeout(() => {
        if (this.currentView === 'opportunities') {
          this.opportunityPage = 1;
          this.loadOpportunities();
        } else if (this.currentView === 'index' && this.historyMode === 'batches' && !this.showJobsPanel) {
          this.batchPage = 1;
          this.loadHistory();
        }
      }, 300);
    };
    window.addEventListener('resize', this._onResize);
    await this.loadConfig();
    if (!this.configError) {
      await this.loadJobs();
      await this.loadCategories();
      await this.loadOverview();
    }
    this.startAutoRefresh();
  },

  beforeUnmount() {
    window.removeEventListener('keydown', this._onKeydown);
    window.removeEventListener('resize', this._onResize);
    this.stopAutoRefresh();
  },

  methods: {
    _batchStats(batch) {
      const batchStart = new Date(batch.startedUtc).getTime();
      const runtimeMs = this.now - batchStart;
      const batchPhase = batch.batchPhase || null;

      const totalFound = batch.totalListingsFound || 0;
      const filtered = batch.totalListingsFilteredPreQueue || 0;
      const skipped = batch.totalListingsSkipped || 0;
      const addedActive = batch.totalListingsAddedActive || 0;
      const addedSold = batch.totalListingsAddedSold || 0;
      const updated = batch.totalListingsUpdated || 0;
      const failed = batch.totalListingsFailed || 0;

      const batchDone = addedActive + addedSold;
      const batchTotal = totalFound - filtered - updated - skipped;

      const allProcessed = addedActive + addedSold + updated + failed;
      const allToProcess = totalFound - filtered - skipped;
      const allRemaining = Math.max(0, allToProcess - allProcessed);
      const processingStart = batch.processingStartedUtc
        ? new Date(batch.processingStartedUtc).getTime()
        : 0;
      const processingSec = processingStart ? (this.now - processingStart) / 1000 : 0;
      const rate = processingSec > 0 ? allProcessed / processingSec : 0;
      const etaSec = rate > 0 ? allRemaining / rate : 0;

      // Count run statuses
      const runs = batch.runs || [];
      const completed = runs.filter(r => r.status === 'Completed').length;
      const running = runs.filter(r => r.status === 'Running').length;
      const queued = runs.filter(r => r.status === 'Queued').length;
      const searching = runs.filter(r => r.status === 'Searching').length;

      return {
        runCount: batch.runCount || 0,
        batchPhase,
        currentPostStage: batch.currentPostStage || null,
        runtimeMs,
        totalProcessed: batchDone,
        totalToProcess: batchTotal,
        remaining: Math.max(0, batchTotal - batchDone),
        rate,
        etaSec,
        completed,
        running,
        queued,
        searching,
      };
    },

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
      this.overviewLoading = true;
      this.overviewError = null;
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
        const controller = new AbortController();
        const timeout = setTimeout(() => controller.abort(), 15000);
        try {
          const data = await this.apiCall(`/overview?${params.toString()}`, { signal: controller.signal });
          this.overviewData = this.toCamelCase(data);
          this.$nextTick(() => {
            this.renderCharts();
            this.overviewLoading = false;
          });
        } finally {
          clearTimeout(timeout);
        }
      } catch (err) {
        if (err.name === 'AbortError') {
          this.overviewError = 'Dashboard query timed out. The database may be busy during a scrape.';
        } else {
          this.overviewError = `Failed to load: ${err.message}`;
        }
        this.overviewLoading = false;
      }
    },

    renderCharts() {
      this.renderCumulativeGrowthChart();
      this.renderOpportunityTrendChart();
      this.renderAvgProfitByConditionChart();
      this.renderTopJobsChart();
      this.renderPriceVsProfitChart();
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

    renderTopJobsChart() {
      const canvas = document.getElementById('topJobsChart');
      if (!canvas) { return; }

      if (this.overviewCharts.topJobs) {
        this.overviewCharts.topJobs.destroy();
      }

      const data = this.overviewData.topJobsByOpportunities || [];
      this.overviewCharts.topJobs = new Chart(canvas, {
        type: 'bar',
        data: {
          labels: data.map(d => this.truncate(d.searchTerm, 15)),
          datasets: [{
            data: data.map(d => d.opportunityCount),
            backgroundColor: '#06b6d4',
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

      this.overviewCharts.avgProfitByCondition = new Chart(canvas, {
        type: 'bar',
        data: {
          labels: data.map(d => conditionLabels[d.condition] || d.condition),
          datasets: [{
            data: data.map(d => d.avgProfit),
            backgroundColor: '#4a9eff',
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
          indexAxis: 'y',
          scales: {
            x: {
              ticks: {
                color: '#808080',
                callback: (v) => this.formatPrice(v, 'GBP')
              },
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
              title: { display: true, text: 'Buy Price', color: '#808080' }
            },
            y: {
              ticks: {
                color: '#808080',
                callback: (v) => `£${v}`
              },
              grid: { color: '#3c3c3c' },
              title: { display: true, text: 'Profit', color: '#808080' },
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

    sortJobs(column) {
      if (this.jobSortBy === column) {
        this.jobSortDir = this.jobSortDir === 'asc' ? 'desc' : 'asc';
      } else {
        this.jobSortBy = column;
        this.jobSortDir = column === 'searchTerm' ? 'asc' : 'desc';
      }
      this.jobPage = 1;
    },

    jobSortIndicator(column) {
      if (this.jobSortBy !== column) { return ''; }
      return this.jobSortDir === 'asc' ? ' \u25B2' : ' \u25BC';
    },

    async loadJobs() {
      try {
        const data = await this.apiCall('/jobs');
        this.jobs = this.toCamelCase(data);
      } catch (err) {
        this.showToast(`Failed to load jobs: ${err.message}`, 'error');
      }
    },

    async loadCategories() {
      try {
        const data = await this.apiCall('/categories');
        this.categories = this.toCamelCase(data);
      } catch (err) {
        this.showToast(`Failed to load categories: ${err.message}`, 'error');
      }
    },

    async createCategory() {
      if (!this.newCategoryName.trim()) { return; }
      try {
        const data = await this.apiCall('/categories', {
          method: 'POST',
          body: JSON.stringify({ name: this.newCategoryName.trim() })
        });
        this.categories.push(this.toCamelCase(data));
        this.newCategoryName = '';
        this.showNewCategoryForm = false;
        this.showToast('Category created', 'success');
      } catch (err) {
        this.showToast(`Failed to create category: ${err.message}`, 'error');
      }
    },

    async renameCategory(cat) {
      const newName = prompt('Rename category:', cat.name);
      if (!newName || newName.trim() === cat.name) { return; }
      try {
        const data = await this.apiCall(`/categories/${cat.id}`, {
          method: 'PUT',
          body: JSON.stringify({ name: newName.trim() })
        });
        const updated = this.toCamelCase(data);
        const idx = this.categories.findIndex(c => c.id === cat.id);
        if (idx !== -1) { this.categories[idx] = updated; }
        this.showToast('Category renamed', 'success');
      } catch (err) {
        this.showToast(`Failed to rename: ${err.message}`, 'error');
      }
    },

    async deleteCategory(cat) {
      if (!confirm(`Delete category "${cat.name}"? Jobs will NOT be deleted.`)) { return; }
      try {
        await this.apiCall(`/categories/${cat.id}`, { method: 'DELETE' });
        this.categories = this.categories.filter(c => c.id !== cat.id);
        this.showToast('Category deleted', 'success');
        await this.loadJobs();
      } catch (err) {
        this.showToast(`Failed to delete: ${err.message}`, 'error');
      }
    },

    async toggleCategory(cat) {
      try {
        const endpoint = cat.isEnabled ? `/categories/${cat.id}/disable` : `/categories/${cat.id}/enable`;
        const data = await this.apiCall(endpoint, { method: 'POST' });
        const updated = this.toCamelCase(data);
        const idx = this.categories.findIndex(c => c.id === cat.id);
        if (idx !== -1) { this.categories[idx] = updated; }
        this.showToast(`Category ${updated.isEnabled ? 'enabled' : 'disabled'}`, 'success');
      } catch (err) {
        this.showToast(`Failed to toggle category: ${err.message}`, 'error');
      }
    },

    toggleCategoryExpand(catId) {
      this.expandedCategories[catId] = !this.expandedCategories[catId];
    },

    jobsInCategory(catId) {
      return this.jobs.filter(j => j.categories?.some(c => c.id === catId));
    },

    uncategorizedJobs() {
      return this.jobs.filter(j => !j.categories || j.categories.length === 0);
    },

    async removeJobFromCategory(jobId, catId) {
      const job = this.jobs.find(j => j.id === jobId);
      if (!job) { return; }
      const newCatIds = (job.categories || []).filter(c => c.id !== catId).map(c => c.id);
      try {
        await this.apiCall(`/jobs/${jobId}/categories`, {
          method: 'POST',
          body: JSON.stringify(newCatIds)
        });
        job.categories = job.categories.filter(c => c.id !== catId);
        const cat = this.categories.find(c => c.id === catId);
        if (cat) { cat.jobCount = Math.max(0, cat.jobCount - 1); }
        this.showToast('Job removed from category', 'success');
      } catch (err) {
        this.showToast(`Failed to remove: ${err.message}`, 'error');
      }
    },

    async addJobToCategory(jobId, catId) {
      const job = this.jobs.find(j => j.id === jobId);
      if (!job) { return; }
      const currentCatIds = (job.categories || []).map(c => c.id);
      if (currentCatIds.includes(catId)) { return; }
      try {
        await this.apiCall(`/jobs/${jobId}/categories`, {
          method: 'POST',
          body: JSON.stringify([...currentCatIds, catId])
        });
        const cat = this.categories.find(c => c.id === catId);
        if (cat) {
          job.categories = [...(job.categories || []), { id: cat.id, name: cat.name }];
          cat.jobCount = (cat.jobCount || 0) + 1;
        }
        this.showToast('Job added to category', 'success');
      } catch (err) {
        this.showToast(`Failed to add: ${err.message}`, 'error');
      }
    },

    async loadHistory(showLoading = true) {
      if (showLoading) {
        this.historyLoading = true;
      }
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
        // If viewing a batch detail, refresh it too
        if (this.selectedBatch) {
          await this.loadBatchDetail(this.selectedBatch.batchId, false);
        }
      } catch (err) {
        this.showToast(`Failed to load history: ${err.message}`, 'error');
      } finally {
        this.historyLoading = false;
      }
    },

    async loadBatchDetail(batchId, showLoading = true) {
      if (showLoading) {
        this.historyLoading = true;
      }
      try {
        const data = await this.apiCall(`/history/batches/${batchId}`);
        this.selectedBatch = this.toCamelCase(data);
      } catch (err) {
        this.showToast(`Failed to load batch detail: ${err.message}`, 'error');
      } finally {
        if (showLoading) {
          this.historyLoading = false;
        }
      }
    },

    async selectBatch(batch) {
      this.historyMode = 'runs';
      this.expandedRuns = {};
      this.runIssues = {};
      await this.loadBatchDetail(batch.batchId);
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
      this.opportunitiesLoading = true;
      try {
        const opp = this.settings?.opportunities || this.config?.opportunities || {};
        const params = new URLSearchParams({
          page: this.opportunityPage,
          pageSize: this.opportunityPageSize,
          sortBy: this.opportunitySortBy,
          sortDir: this.opportunitySortDir
        });
        let effectiveJobIds = [];
        if (this.opportunityJobFilter.length > 0) {
          effectiveJobIds = this.opportunityJobFilter;
        } else if (this.filteredJobIds) {
          effectiveJobIds = this.filteredJobIds;
        }
        if (effectiveJobIds.length > 0) {
          params.set('jobIds', effectiveJobIds.join(','));
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
        if (this.opportunityBudget > 0) {
          params.set('maxPrice', this.opportunityBudget);
        }
        if (this.opportunitySearchQuery.trim()) {
          params.set('searchQuery', this.opportunitySearchQuery.trim());
          this.opportunitySearchLoading = true;
        }
        const data = await this.apiCall(`/listings/active?${params.toString()}`);
        const result = this.toCamelCase(data);
        this.opportunities = result.items;
        this.opportunityTotalCount = result.totalCount;
        this.opportunityTotalPages = result.totalPages;
      } catch (err) {
        this.showToast(`Failed to load opportunities: ${err.message}`, 'error');
      } finally {
        this.opportunitiesLoading = false;
        this.opportunitySearchLoading = false;
      }
    },

    searchOpportunities() {
      this.opportunityPage = 1;
      this.loadOpportunities();
    },

    clearSearch() {
      this.opportunitySearchQuery = '';
      this.opportunityPage = 1;
      this.loadOpportunities();
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

    isCategoryFiltered(catId) {
      return this.opportunityCategoryFilter.includes(catId);
    },

    toggleCategoryFilter(catId) {
      const idx = this.opportunityCategoryFilter.indexOf(catId);
      if (idx >= 0) {
        this.opportunityCategoryFilter.splice(idx, 1);
      } else {
        this.opportunityCategoryFilter.push(catId);
      }
      this.opportunityJobFilter = [];
      this.opportunityPage = 1;
      this.loadOpportunities();
    },

    clearCategoryFilter() {
      this.opportunityCategoryFilter = [];
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

    goToCompPage(page) {
      if (page < 1 || page > this.compTotalPages) { return; }
      this.compPage = page;
    },

    listingDetailParams() {
      const opp = this.settings?.opportunities || {};
      const params = new URLSearchParams();
      if (opp.priceBandMultiplier > 0) {
        params.set('priceBand', opp.priceBandMultiplier);
      }
      if (opp.feePercent > 0) {
        params.set('feePercent', opp.feePercent);
      }
      if (opp.matchCondition) {
        params.set('matchCondition', 'true');
      }
      return params.toString();
    },

    async loadListingDetail(id) {
      this.listingDetailLoading = true;
      this.compPage = 1;
      this.descriptionExpanded = false;
      this.closeCompare();
      try {
        const qs = this.listingDetailParams();
        const data = await this.apiCall(`/listings/${id}${qs ? '?' + qs : ''}`);
        this.listingDetail = this.toCamelCase(data);
      } catch (err) {
        this.showToast(`Failed to load listing: ${err.message}`, 'error');
      } finally {
        this.listingDetailLoading = false;
      }
    },

    openListingDetail(listing) {
      this.selectedListingId = listing.id;
      this.currentView = 'listing-detail';
      this.loadListingDetail(listing.id);
    },

    backToOpportunities() {
      this.currentView = 'opportunities';
      this.listingDetail = null;
      this.selectedListingId = null;
    },

    async dismissComparable(relationshipId) {
      const confirmed = await this.showConfirm(
        'Dismiss Comparable',
        'Remove this comparable from the analysis? This will affect pricing calculations.',
        'Dismiss'
      );
      if (!confirmed) { return; }

      try {
        const qs = this.listingDetailParams();
        const data = await this.apiCall(
          `/listings/${this.selectedListingId}/comparables/${relationshipId}${qs ? '?' + qs : ''}`,
          { method: 'DELETE' }
        );
        this.listingDetail = this.toCamelCase(data);
        const count = this.listingDetail.comparables?.length || 0;
        this.showToast(`Comparable dismissed. ${count} remaining.`, 'success');
      } catch (err) {
        this.showToast(`Failed to dismiss: ${err.message}`, 'error');
      }
    },

    openCompare(comp) {
      const idx = this.listingDetail.comparables.findIndex(c => c.relationshipId === comp.relationshipId);
      this.compareIndex = idx >= 0 ? idx : 0;
      this.compareComp = this.listingDetail.comparables[this.compareIndex];
    },

    closeCompare() {
      this.compareComp = null;
      this.compareIndex = -1;
    },

    compareNav(direction) {
      const next = this.compareIndex + direction;
      if (next >= 0 && next < this.listingDetail.comparables.length) {
        this.compareIndex = next;
        this.compareComp = this.listingDetail.comparables[next];
      }
    },

    async dismissAndNav(relationshipId) {
      const compsBeforeDismiss = this.listingDetail.comparables.length;
      await this.dismissComparable(relationshipId);
      const comps = this.listingDetail.comparables;
      if (comps.length === 0) {
        this.closeCompare();
        return;
      }
      // Stay at same index, or clamp to end
      const idx = Math.min(this.compareIndex, comps.length - 1);
      this.compareIndex = idx;
      this.compareComp = comps[idx];
    },

    parseImages(imagesJson) {
      if (!imagesJson) { return []; }
      try {
        const parsed = JSON.parse(imagesJson);
        return Array.isArray(parsed) ? parsed : [];
      } catch {
        return [];
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
        isEnabled: job.isEnabled,
        categoryIds: (job.categories || []).map(c => c.id)
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
        await this.loadJobs();
        await this.loadCategories();
      } catch (err) {
        this.showToast(`Failed to save job: ${err.message}`, 'error');
      }
    },

    closeJobForm() {
      this.showJobForm = false;
      this.editingJob = null;
      this.jobForm = { searchTerm: '', filterInstructions: '', isEnabled: true, categoryIds: [] };
    },

    async startScrape() {
      this.loading = true;
      try {
        const data = await this.apiCall('/scrape/start', {
          method: 'POST',
          timeout: 120000
        });
        const result = this.toCamelCase(data);
        const runCount = result.runCount || 0;
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

    formatShortDate(dateStr) {
      if (!dateStr) return '-';
      const date = new Date(dateStr);
      return date.toLocaleDateString('en-GB', { day: '2-digit', month: 'short', year: 'numeric' });
    },

    searchedJobCount(batch) {
      // Use server-aggregated count, fallback to runs if available
      if (batch.searchedJobCount != null) { return batch.searchedJobCount; }
      if (!batch.runs) { return 0; }
      return batch.runs.filter(r => r.totalListingsFound > 0).length;
    },

    // Progress calculations — logic lives in progress.js (shared with tests).
    // These delegate to the same formulas used there.
    activeSoldDone(run) {
      return (run.listingsAddedActive || 0) + (run.listingsAddedSold || 0);
    },

    activeSoldTotal(run) {
      return (run.totalListingsFound || 0) - (run.listingsFilteredPreQueue || 0)
        - (run.listingsUpdated || 0) - (run.listingsSkipped || 0);
    },

    progressPercent(run) {
      const total = this.activeSoldTotal(run);
      const done = this.activeSoldDone(run);
      if (total <= 0) { return done > 0 ? 100 : 0; }
      return Math.min(100, Math.round((done / total) * 100));
    },

    batchDone(batch) {
      if (batch.totalListingsAddedActive != null) {
        return (batch.totalListingsAddedActive || 0) + (batch.totalListingsAddedSold || 0);
      }
      return (batch.runs || []).reduce((s, r) => s + this.activeSoldDone(r), 0);
    },

    batchTotal(batch) {
      if (batch.totalListingsAddedActive != null) {
        return (batch.totalListingsFound || 0) - (batch.totalListingsFilteredPreQueue || 0)
          - (batch.totalListingsUpdated || 0) - (batch.totalListingsSkipped || 0);
      }
      return (batch.runs || []).reduce((s, r) => s + this.activeSoldTotal(r), 0);
    },

    batchProgressPercent(batch) {
      const total = this.batchTotal(batch);
      const done = this.batchDone(batch);
      if (total <= 0) { return done > 0 ? 100 : 0; }
      return Math.min(100, Math.round((done / total) * 100));
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
        if (this.showJobsPanel) {
          this.loadJobs();
        }
        if (this.currentView === 'index') {
          const activeStatuses = ['Queued', 'Running', 'Indexing', 'Searching', 'Processing'];
          const hasActive = this.batches.some(b =>
            ['Running', 'Queued'].includes(b.status));
          if (hasActive) {
            this.loadHistory(false);
            // Also refresh issues for any expanded active runs in the selected batch
            if (this.selectedBatch) {
              (this.selectedBatch.runs || [])
                .filter(r => activeStatuses.includes(r.status) && this.expandedRuns[r.id])
                .forEach(r => this.loadRunIssues(r.id));
            }
          }
        }
      }, 1000);
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

    showConfirm(title, message, confirmLabel = 'Confirm') {
      return new Promise(resolve => {
        this.confirmDialog = { title, message, confirmLabel, resolve };
      });
    },

    resolveConfirm(result) {
      if (this.confirmDialog?.resolve) {
        this.confirmDialog.resolve(result);
      }
      this.confirmDialog = null;
    },

    toggleSection(section) {
      const isOpening = this.collapsedSections[section];
      // Accordion: close all sections first
      Object.keys(this.collapsedSections).forEach(key => {
        this.collapsedSections[key] = true;
      });
      // Toggle the clicked section
      if (isOpening) {
        this.collapsedSections[section] = false;
      }
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
    },

    // ── Markets tab methods ──────────────────────────────────

    async loadMarkets() {
      this.marketsLoading = true;
      this.marketsError = null;
      try {
        this.marketsData = await this.apiCall('/markets');
      } catch (error) {
        this.marketsError = error.message;
      } finally {
        this.marketsLoading = false;
      }
    },

    clearMarketsFilters() {
      this.marketsListingSearch = '';
      this.marketsStatusFilter = '';
      this.marketsConditionFilter = '';
      this.marketsMinPrice = '';
      this.marketsMaxPrice = '';
      this.marketsMinDays = '';
      this.marketsMaxDays = '';
      this.marketsRegex = '';
      this.marketsRegexError = '';
      this.marketsListingPage = 1;
      this.loadMarketListings();
    },

    _debouncedMarketsReload() {
      clearTimeout(this._marketsFilterTimer);
      this._marketsFilterTimer = setTimeout(() => {
        this.marketsListingPage = 1;
        this.loadMarketListings();
      }, 400);
    },

    async openMarketJob(job) {
      this.marketsSelected = job;
      this.marketsListingSearch = '';
      this.marketsStatusFilter = '';
      this.marketsConditionFilter = '';
      this.marketsMinPrice = '';
      this.marketsMaxPrice = '';
      this.marketsMinDays = '';
      this.marketsMaxDays = '';
      this.marketsRegex = '';
      this.marketsRegexError = '';
      this.marketsShowFilters = false;
      this.marketsChatOpen = false;
      this.marketsChatMessages = [];
      this.marketsChatInput = '';
      this.marketsChatLoading = false;
      this.marketsListingSortKey = 'daysOnMarket';
      this.marketsListingSortDir = 1;
      this.marketsListingPage = 1;
      await this.loadMarketListings();
    },

    async loadMarketListings() {
      this.marketsListingsLoading = true;
      this.marketsRegexError = '';
      try {
        const params = new URLSearchParams();
        params.set('page', this.marketsListingPage);
        params.set('pageSize', this.marketsListingPageSize);
        params.set('sortBy', this.marketsListingSortKey);
        params.set('sortDir', this.marketsListingSortDir === 1 ? 'asc' : 'desc');
        if (this.marketsStatusFilter) {
          params.set('status', this.marketsStatusFilter);
        }
        if (this.marketsListingSearch) {
          params.set('search', this.marketsListingSearch);
        }
        if (this.marketsConditionFilter) {
          params.set('condition', this.marketsConditionFilter);
        }
        if (this.marketsMinPrice) {
          params.set('minPrice', this.marketsMinPrice);
        }
        if (this.marketsMaxPrice) {
          params.set('maxPrice', this.marketsMaxPrice);
        }
        if (this.marketsMinDays) {
          params.set('minDays', this.marketsMinDays);
        }
        if (this.marketsMaxDays) {
          params.set('maxDays', this.marketsMaxDays);
        }
        if (this.marketsRegex) {
          params.set('regex', this.marketsRegex);
        }
        const data = await this.apiCall(
          `/markets/${this.marketsSelected.jobId}/listings?${params.toString()}`
        );
        this.marketsListings = data.items;
        this.marketsListingTotal = data.totalCount;
        this.marketsListingStats = data.stats;
      } catch (error) {
        if (error.message?.includes('regex') || error.message?.includes('Invalid')) {
          this.marketsRegexError = 'Invalid regex pattern';
        } else {
          this.marketsError = error.message;
        }
      } finally {
        this.marketsListingsLoading = false;
      }
    },

    backToMarkets() {
      this.marketsSelected = null;
      this.marketsListings = [];
    },

    setMarketsSort(key) {
      if (this.marketsSortKey === key) {
        this.marketsSortDir *= -1;
      } else {
        this.marketsSortKey = key;
        this.marketsSortDir = key === 'searchTerm' ? 1 : -1;
      }
    },

    setMarketsListingSort(key) {
      if (this.marketsListingSortKey === key) {
        this.marketsListingSortDir *= -1;
      } else {
        this.marketsListingSortKey = key;
        this.marketsListingSortDir = (key === 'title' || key === 'listingStatus' || key === 'condition') ? 1 : -1;
      }
      this.marketsListingPage = 1;
      this.loadMarketListings();
    },

    marketsStClass(val) {
      if (val >= 40) { return 'st-high'; }
      if (val >= 25) { return 'st-mid'; }
      return 'st-low';
    },

    marketsDaysClass(val) {
      if (val == null) { return ''; }
      if (val <= 7) { return 'st-high'; }
      if (val <= 21) { return 'st-mid'; }
      return 'st-low';
    },

    // Chat panel methods
    toggleMarketsChat() {
      this.marketsChatOpen = !this.marketsChatOpen;
      if (this.marketsChatOpen && this.marketsChatMessages.length === 0) {
        const term = this.marketsSelected?.searchTerm || 'this category';
        this.marketsChatMessages.push({
          role: 'assistant',
          text: `I can help you isolate specific variants within "${term}". Describe what you're looking for — e.g. "825GB console only, no bundles" — and I'll build the filter.`,
          time: new Date()
        });
      }
      this.$nextTick(() => this._scrollChatToBottom());
    },

    async sendChatMessage() {
      const text = this.marketsChatInput.trim();
      if (!text || this.marketsChatLoading) { return; }

      this.marketsChatMessages.push({ role: 'user', text, time: new Date() });
      this.marketsChatInput = '';
      this.marketsChatLoading = true;
      this.$nextTick(() => this._scrollChatToBottom());

      // Stub: simulate API call delay
      await new Promise(r => setTimeout(r, 800 + Math.random() * 1200));

      this.marketsChatMessages.push({
        role: 'assistant',
        text: this._stubChatResponse(text),
        time: new Date()
      });
      this.marketsChatLoading = false;
      this.$nextTick(() => this._scrollChatToBottom());
    },

    handleChatKeydown(e) {
      if (e.key === 'Enter' && !e.shiftKey) {
        e.preventDefault();
        this.sendChatMessage();
      }
    },

    formatChatMessage(text) {
      return text.replace(/`([^`]+)`/g, '<code>$1</code>');
    },

    chatTimeLabel(time) {
      if (!time) { return ''; }
      const d = new Date(time);
      return d.toLocaleTimeString('en-GB', { hour: '2-digit', minute: '2-digit' });
    },

    _stubChatResponse(userText) {
      const lower = userText.toLowerCase();
      if (lower.includes('console') || lower.includes('disc') || lower.includes('digital')) {
        return 'I\'d suggest filtering with:\n\n`^(?!.*(bundle|case|controller|\\d+\\s*x)).*console.*disc`\n\nThis matches "console" + "disc" while excluding bundles, cases, controllers, and multi-packs. Want me to apply this?';
      }
      if (lower.includes('no bundle') || lower.includes('exclude') || lower.includes('without')) {
        return 'To exclude bundles and multi-packs, I\'ll add negative lookaheads:\n\n`^(?!.*(bundle|lot|set|\\d+\\s*x|x\\s*\\d+))`\n\nShould I combine this with your existing filter or replace it?';
      }
      if (lower.includes('apply') || lower.includes('yes') || lower.includes('try')) {
        return 'Filter applied. The results are updating now. Check the stats strip above — if the price spread looks tight, the cluster is clean. Want me to help tighten it further?';
      }
      if (lower.includes('etb') || lower.includes('elite trainer') || lower.includes('booster')) {
        return 'For Pokemon TCG, I\'d start with:\n\n`^(?!.*(case|bundle|booster|pok.mon\\s*cent|\\d+\\s*x)).*phantasmal.*flames.*(elite.trainer|ETB)`\n\nThis isolates single ETBs and excludes cases, bundles, booster packs, Pokemon Center exclusives, and multi-packs.';
      }
      return 'I can help with that. Could you be more specific about which variant?\n\n- What model, size, or storage?\n- Exclude bundles, cases, or multi-packs?\n- Any condition preference (new, used, refurbished)?';
    },

    _scrollChatToBottom() {
      const el = this.$el?.querySelector('.chat-messages');
      if (el) { el.scrollTop = el.scrollHeight; }
    },
  }
}).mount('#app');
