const { createApp } = Vue;

createApp({
  data() {
    return {
      currentView: 'jobs',
      config: null,
      configError: null,
      jobs: [],
      opportunities: [],
      history: [],
      expandedRuns: {},
      runIssues: {},
      loading: false,
      toast: null,
      showJobForm: false,
      editingJob: null,
      jobForm: {
        searchTerm: '',
        filterInstructions: '',
        isEnabled: true
      },
      lastInstanceId: localStorage.getItem('lastInstanceId') || null,
      refreshInterval: null,
      settings: {
        marketMakerApi: { baseUrl: '', functionKey: '' },
        etlApi: { baseUrl: '' },
        scraperApi: { baseUrl: '' },
        scraping: { maxSoldListings: null, maxActiveListings: null, defaultLookbackDays: 180 },
        storage: { connectionString: '' },
        openAi: { apiKey: '', model: '' },
        pinecone: { apiKey: '', indexName: '' }
      },
      collapsedSections: {
        api: true,
        scraping: true,
        storage: true,
        openAi: true,
        pinecone: true
      },
      savingSettings: false
    };
  },

  computed: {
    apiTarget() {
      if (!this.config?.marketMakerApi?.baseUrl) return 'Not configured';
      const url = this.config.marketMakerApi.baseUrl;
      if (url.includes('localhost') || url.includes('127.0.0.1')) return 'Local';
      return 'Production';
    }
  },

  async mounted() {
    await this.loadConfig();
    if (!this.configError) {
      await this.loadJobs();
    }
    this.startAutoRefresh();
  },

  beforeUnmount() {
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

      const url = new URL(`${baseUrl}${endpoint}`);

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
        const data = await this.apiCall('/history');
        this.history = this.toCamelCase(data);
      } catch (err) {
        this.showToast(`Failed to load history: ${err.message}`, 'error');
      }
    },

    async loadOpportunities() {
      try {
        const data = await this.apiCall('/listings/active');
        this.opportunities = this.toCamelCase(data);
      } catch (err) {
        this.showToast(`Failed to load opportunities: ${err.message}`, 'error');
      }
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
      return `${symbol}${price.toFixed(2)}`;
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
        const body = {};
        if (this.settings.scraping?.maxSoldListings) {
          body.maxSoldListings = this.settings.scraping.maxSoldListings;
        }
        if (this.settings.scraping?.maxActiveListings) {
          body.maxActiveListings = this.settings.scraping.maxActiveListings;
        }
        if (this.settings.scraping?.defaultLookbackDays) {
          body.lookbackDays = this.settings.scraping.defaultLookbackDays;
        }

        const data = await this.etlApiCall('/scrape/start', {
          method: 'POST',
          body: Object.keys(body).length > 0 ? JSON.stringify(body) : undefined
        });
        const result = this.toCamelCase(data);
        this.lastInstanceId = result.instanceId;
        localStorage.setItem('lastInstanceId', result.instanceId);
        this.showToast(`Scrape started (Run #${result.runId})`, 'success');

        // Switch to history view to see progress
        this.currentView = 'history';
        await this.loadHistory();
      } catch (err) {
        this.showToast(`Failed to start scrape: ${err.message}`, 'error');
      } finally {
        this.loading = false;
      }
    },

    async terminateOrchestration() {
      if (!this.lastInstanceId) return;
      if (!confirm('Terminate this orchestration?')) return;

      try {
        await this.etlApiCall(`/orchestration/${this.lastInstanceId}`, { method: 'DELETE' });
        this.showToast('Orchestration terminated', 'success');
        this.lastInstanceId = null;
        localStorage.removeItem('lastInstanceId');
      } catch (err) {
        this.showToast(`Failed to terminate: ${err.message}`, 'error');
      }
    },

    async purgeOrchestrations() {
      if (!confirm('Purge all orchestrations from the last 7 days?')) return;

      try {
        const data = await this.etlApiCall('/orchestration/purge', { method: 'POST' });
        const result = this.toCamelCase(data);
        this.showToast(`Purged ${result.purged} orchestrations`, 'success');
      } catch (err) {
        this.showToast(`Failed to purge: ${err.message}`, 'error');
      }
    },

    async clearAllData() {
      if (!confirm('Delete ALL scrape data (listings, run history, and tracking data)? This cannot be undone.')) return;

      this.loading = true;
      try {
        const data = await this.apiCall('/data/all', { method: 'DELETE' });
        const result = this.toCamelCase(data);
        this.showToast(`Cleared ${result.deletedListings} listings and ${result.deletedRuns} history records`, 'success');
        if (this.currentView === 'history') {
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
        this.showToast(`Cleared ${result.deleted} listings`, 'success');
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
        if (this.currentView === 'history') {
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

    progressPercent(run) {
      if (!run.totalListingsFound || run.totalListingsFound === 0) return 0;
      return Math.round((run.listingsProcessed / run.totalListingsFound) * 100);
    },

    startAutoRefresh() {
      this.refreshInterval = setInterval(() => {
        if (this.currentView === 'history') {
          const hasRunning = this.history.some(r => r.status === 'Running');
          if (hasRunning) {
            this.loadHistory();
            // Also refresh issues for any expanded running runs
            this.history
              .filter(r => r.status === 'Running' && this.expandedRuns[r.id])
              .forEach(r => this.loadRunIssues(r.id));
          }
        }
      }, 2000);
    },

    stopAutoRefresh() {
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
      if (!run.issueCount || run.issueCount === 0) return;

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
