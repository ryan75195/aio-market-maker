const { createApp } = Vue;

createApp({
  data() {
    return {
      currentView: 'jobs',
      config: null,
      configError: null,
      jobs: [],
      loading: false,
      toast: null,
      showJobForm: false,
      editingJob: null,
      jobForm: {
        searchTerm: '',
        filterInstructions: '',
        isEnabled: true
      },
      lastInstanceId: localStorage.getItem('lastInstanceId') || null
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

    async loadJobs() {
      try {
        this.jobs = await this.apiCall('/jobs');
      } catch (err) {
        this.showToast(`Failed to load jobs: ${err.message}`, 'error');
      }
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
          const updated = await this.apiCall(`/jobs/${this.editingJob.id}`, {
            method: 'PUT',
            body: JSON.stringify(this.jobForm)
          });
          const index = this.jobs.findIndex(j => j.id === this.editingJob.id);
          if (index !== -1) this.jobs[index] = updated;
          this.showToast('Job updated', 'success');
        } else {
          const created = await this.apiCall('/jobs', {
            method: 'POST',
            body: JSON.stringify(this.jobForm)
          });
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
        const result = await this.apiCall('/scrape/start', { method: 'POST' });
        this.lastInstanceId = result.instanceId;
        localStorage.setItem('lastInstanceId', result.instanceId);
        this.showToast(`Scrape started: ${result.instanceId}`, 'success');
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
        await this.apiCall(`/orchestration/${this.lastInstanceId}`, { method: 'DELETE' });
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
        const result = await this.apiCall('/orchestration/purge', { method: 'POST' });
        this.showToast(`Purged ${result.purged} orchestrations`, 'success');
      } catch (err) {
        this.showToast(`Failed to purge: ${err.message}`, 'error');
      }
    },

    formatDate(dateStr) {
      if (!dateStr) return 'Never';
      const date = new Date(dateStr);
      return date.toLocaleString();
    },

    showToast(message, type = 'info') {
      this.toast = { message, type };
      setTimeout(() => {
        this.toast = null;
      }, 4000);
    }
  }
}).mount('#app');
