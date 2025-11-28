// Dashboard JavaScript

// Global state
let currentPage = 1;
let metricsData = null;
let charts = {};
let searchTimeout = null;

// Job lookup will be populated from server data
let jobsLookup = {};

// Initialize on page load
document.addEventListener('DOMContentLoaded', () => {
    // Load jobs lookup from data attribute
    const jobsData = document.getElementById('jobsData');
    if (jobsData) {
        try {
            jobsLookup = JSON.parse(jobsData.dataset.jobs || '{}');
        } catch (e) {
            console.error('Failed to parse jobs data:', e);
        }
    }
    loadDashboardData();
});

// Tab switching
function switchTab(tabName) {
    document.querySelectorAll('.tab').forEach(t => t.classList.remove('active'));
    document.querySelectorAll('.tab-content').forEach(c => c.classList.remove('active'));

    document.querySelector(`.tab[onclick="switchTab('${tabName}')"]`).classList.add('active');
    document.getElementById(tabName + '-tab').classList.add('active');

    if (tabName === 'dashboard' && !metricsData) {
        loadDashboardData();
    }
    if (tabName === 'products') {
        loadProducts(1);
    }
}

// Dashboard data and charts
async function loadDashboardData() {
    try {
        const res = await fetch('/api/metrics');
        metricsData = await res.json();
        renderDashboardStats();
        renderCharts();
    } catch (e) {
        console.error('Failed to load metrics:', e);
    }
}

function renderDashboardStats() {
    const m = metricsData;
    document.getElementById('dashboardStats').innerHTML = `
        <div class="stat-card">
            <div class="value">${m.jobPerformance.totalProducts}</div>
            <div class="label">Total Products</div>
        </div>
        <div class="stat-card success">
            <div class="value">${m.salesVolume.totalSold}</div>
            <div class="label">Sold Items</div>
        </div>
        <div class="stat-card">
            <div class="value">${m.salesVolume.totalActive}</div>
            <div class="label">Active Listings</div>
        </div>
        <div class="stat-card warning">
            <div class="value">${m.priceAnalytics.avgPrice.toFixed(2)}</div>
            <div class="label">Avg Price</div>
        </div>
        <div class="stat-card success">
            <div class="value">${m.salesVolume.totalRevenue.toFixed(0)}</div>
            <div class="label">Total Revenue</div>
        </div>
        <div class="stat-card">
            <div class="value">${m.jobPerformance.enabledJobs}/${m.jobPerformance.totalJobs}</div>
            <div class="label">Active Jobs</div>
        </div>
    `;
}

function renderCharts() {
    const m = metricsData;

    // Destroy existing charts
    Object.values(charts).forEach(c => c.destroy());
    charts = {};

    // Price Distribution
    charts.price = new Chart(document.getElementById('priceChart'), {
        type: 'bar',
        data: {
            labels: m.priceAnalytics.priceDistribution.map(d => d.range),
            datasets: [{
                label: 'Products',
                data: m.priceAnalytics.priceDistribution.map(d => d.count),
                backgroundColor: '#3b82f6'
            }]
        },
        options: {
            responsive: true,
            maintainAspectRatio: false,
            plugins: { legend: { display: false } }
        }
    });

    // Sales Over Time
    charts.sales = new Chart(document.getElementById('salesChart'), {
        type: 'line',
        data: {
            labels: m.salesVolume.soldByDay.map(d => d.date.substring(5)),
            datasets: [{
                label: 'Items Sold',
                data: m.salesVolume.soldByDay.map(d => d.count),
                borderColor: '#16a34a',
                backgroundColor: 'rgba(22, 163, 74, 0.1)',
                fill: true,
                tension: 0.3
            }]
        },
        options: {
            responsive: true,
            maintainAspectRatio: false,
            plugins: { legend: { display: false } }
        }
    });

    // Revenue by Job
    const revenueColors = ['#3b82f6', '#16a34a', '#f59e0b', '#ef4444', '#8b5cf6', '#ec4899'];
    charts.revenue = new Chart(document.getElementById('revenueChart'), {
        type: 'doughnut',
        data: {
            labels: m.salesVolume.soldByJob.map(d => d.searchTerm),
            datasets: [{
                data: m.salesVolume.soldByJob.map(d => d.revenue),
                backgroundColor: revenueColors
            }]
        },
        options: {
            responsive: true,
            maintainAspectRatio: false
        }
    });

    // Products per Job
    charts.jobs = new Chart(document.getElementById('jobsChart'), {
        type: 'bar',
        data: {
            labels: m.jobPerformance.jobStats.map(d => d.searchTerm.substring(0, 15)),
            datasets: [{
                label: 'Products',
                data: m.jobPerformance.jobStats.map(d => d.productCount),
                backgroundColor: '#8b5cf6'
            }]
        },
        options: {
            responsive: true,
            maintainAspectRatio: false,
            indexAxis: 'y',
            plugins: { legend: { display: false } }
        }
    });
}

// Products tab
async function loadProducts(page) {
    currentPage = page;
    const status = document.getElementById('filterStatus').value;
    const jobId = document.getElementById('filterJob').value;
    const search = document.getElementById('filterSearch').value;
    const pageSize = document.getElementById('pageSize').value;

    const params = new URLSearchParams({ page, pageSize });
    if (status) params.append('status', status);
    if (jobId) params.append('jobId', jobId);
    if (search) params.append('search', search);

    try {
        const res = await fetch('/api/products?' + params);
        const data = await res.json();
        renderProductsTable(data.products);
        renderPagination(data.total, data.page, data.pageSize);
    } catch (e) {
        console.error('Failed to load products:', e);
    }
}

function renderProductsTable(products) {
    const tbody = document.getElementById('productsBody');
    if (products.length === 0) {
        tbody.innerHTML = '<tr><td colspan="8" style="text-align: center; padding: 40px;">No products found</td></tr>';
        return;
    }

    tbody.innerHTML = products.map(p => {
        const statusClass = p.listingStatus === 'Sold' ? 'status-sold' : 'status-active';
        const title = p.title ? (p.title.length > 50 ? p.title.substring(0, 47) + '...' : p.title) : '(no title)';
        const price = p.price ? p.price.toFixed(2) + ' ' + (p.currency || '') : '-';
        const endDate = p.endDateUtc ? p.endDateUtc.substring(0, 10) : '-';
        const jobName = jobsLookup[p.scrapeJobId] || p.scrapeJobId;

        return `<tr>
            <td>${p.id}</td>
            <td><a href="${p.url || '#'}" target="_blank">${p.listingId}</a></td>
            <td class="title-cell" title="${(p.title || '').replace(/"/g, '&quot;')}">${title}</td>
            <td class="price">${price}</td>
            <td class="${statusClass}">${p.listingStatus || '-'}</td>
            <td>${p.condition || '-'}</td>
            <td>${jobName}</td>
            <td>${endDate}</td>
        </tr>`;
    }).join('');
}

function renderPagination(total, page, pageSize) {
    const totalPages = Math.ceil(total / pageSize);
    const pagination = document.getElementById('pagination');

    if (totalPages <= 1) {
        pagination.innerHTML = `<span class="page-info">Showing ${total} products</span>`;
        return;
    }

    let html = `<button class="page-btn" onclick="loadProducts(${page - 1})" ${page <= 1 ? 'disabled' : ''}>Prev</button>`;

    const startPage = Math.max(1, page - 2);
    const endPage = Math.min(totalPages, page + 2);

    if (startPage > 1) html += `<button class="page-btn" onclick="loadProducts(1)">1</button>`;
    if (startPage > 2) html += `<span>...</span>`;

    for (let i = startPage; i <= endPage; i++) {
        html += `<button class="page-btn ${i === page ? 'active' : ''}" onclick="loadProducts(${i})">${i}</button>`;
    }

    if (endPage < totalPages - 1) html += `<span>...</span>`;
    if (endPage < totalPages) html += `<button class="page-btn" onclick="loadProducts(${totalPages})">${totalPages}</button>`;

    html += `<button class="page-btn" onclick="loadProducts(${page + 1})" ${page >= totalPages ? 'disabled' : ''}>Next</button>`;
    html += `<span class="page-info">Page ${page} of ${totalPages} (${total} total)</span>`;

    pagination.innerHTML = html;
}

function debounceSearch() {
    clearTimeout(searchTimeout);
    searchTimeout = setTimeout(() => loadProducts(1), 300);
}

// Job management functions
function showMessage(text, isError) {
    const msg = document.getElementById('message');
    msg.textContent = text;
    msg.className = 'message ' + (isError ? 'error' : 'success');
    setTimeout(() => { msg.className = 'message'; }, 5000);
}

function toggleAddForm() {
    document.getElementById('addJobForm').classList.toggle('show');
}

async function createJob() {
    const searchTerm = document.getElementById('searchTerm').value.trim();
    if (!searchTerm) {
        showMessage('Search term is required', true);
        return;
    }

    const data = {
        searchTerm,
        searchType: document.getElementById('searchType').value,
        buyingFormat: document.getElementById('buyingFormat').value,
        condition: document.getElementById('condition').value,
        frequencyMinutes: parseInt(document.getElementById('frequencyMinutes').value) || 60,
        lookbackDays: parseInt(document.getElementById('lookbackDays').value) || null,
        itemLimit: parseInt(document.getElementById('itemLimit').value) || null,
        isEnabled: true
    };

    try {
        const res = await fetch('/api/jobs', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify(data)
        });
        if (res.ok) {
            showMessage('Job created successfully!', false);
            setTimeout(() => location.reload(), 1000);
        } else {
            const err = await res.json();
            showMessage(err.error || 'Failed to create job', true);
        }
    } catch (e) {
        showMessage('Error: ' + e.message, true);
    }
}

async function runJob(id) {
    if (!confirm('Run this job now? The job will run in the background.')) return;

    const card = document.getElementById('job-' + id);
    card.classList.add('loading');
    showMessage('Starting ETL job...', false);

    try {
        const res = await fetch('/api/etl/run/' + id, { method: 'POST' });
        const result = await res.json();

        if (!res.ok) {
            showMessage('Failed to start job: ' + (result.error || 'Unknown error'), true);
            card.classList.remove('loading');
            return;
        }

        showMessage(`Job started! Instance ID: ${result.instanceId}. Polling for status...`, false);
        await pollJobStatus(result.instanceId, card);
    } catch (e) {
        showMessage('Error: ' + e.message, true);
        card.classList.remove('loading');
    }
}

async function pollJobStatus(instanceId, card) {
    const maxAttempts = 120;
    let attempts = 0;

    const poll = async () => {
        try {
            const res = await fetch('/api/etl/status/' + instanceId);
            const status = await res.json();

            if (status.status === 'Completed') {
                const output = status.output ? JSON.parse(status.output) : null;
                if (output) {
                    showMessage(`Job completed! Found ${output.ListingsFound} listings, fetched ${output.NewListingsFetched} new, saved ${output.ProductsSaved} products.`, false);
                } else {
                    showMessage('Job completed successfully!', false);
                }
                card.classList.remove('loading');
                metricsData = null; // Force refresh
                setTimeout(() => location.reload(), 2000);
                return;
            } else if (status.status === 'Failed') {
                showMessage('Job failed: ' + (status.output || 'Unknown error'), true);
                card.classList.remove('loading');
                return;
            } else if (status.status === 'Terminated') {
                showMessage('Job was terminated', true);
                card.classList.remove('loading');
                return;
            }

            attempts++;
            if (attempts >= maxAttempts) {
                showMessage('Job is still running. Check status at: /api/etl/status/' + instanceId, false);
                card.classList.remove('loading');
                return;
            }

            showMessage(`Job running... (${status.status}) - ${attempts * 5}s elapsed`, false);
            setTimeout(poll, 5000);
        } catch (e) {
            showMessage('Error polling status: ' + e.message, true);
            card.classList.remove('loading');
        }
    };

    await poll();
}

async function toggleJob(id) {
    try {
        const res = await fetch('/api/jobs/' + id + '/toggle', { method: 'POST' });
        if (res.ok) {
            location.reload();
        } else {
            showMessage('Failed to toggle job', true);
        }
    } catch (e) {
        showMessage('Error: ' + e.message, true);
    }
}

async function deleteJob(id) {
    if (!confirm('Delete this job and all its products? This cannot be undone.')) return;

    try {
        const res = await fetch('/api/jobs/' + id, { method: 'DELETE' });
        if (res.ok) {
            showMessage('Job deleted successfully', false);
            setTimeout(() => location.reload(), 1000);
        } else {
            showMessage('Failed to delete job', true);
        }
    } catch (e) {
        showMessage('Error: ' + e.message, true);
    }
}

async function runAllJobs() {
    if (!confirm('Run all enabled jobs? This will process them sequentially in the background.')) return;

    showMessage('Starting all enabled jobs...', false);

    try {
        const res = await fetch('/api/etl/run-all', { method: 'POST' });
        const result = await res.json();

        if (!res.ok) {
            showMessage('Failed to start jobs: ' + (result.error || 'Unknown error'), true);
            return;
        }

        if (result.jobCount === 0) {
            showMessage('No enabled jobs found to run.', false);
            return;
        }

        showMessage(`Started ${result.jobCount} jobs! Instance ID: ${result.instanceId}. Polling for status...`, false);
        await pollAllJobsStatus(result.instanceId);
    } catch (e) {
        showMessage('Error: ' + e.message, true);
    }
}

async function pollAllJobsStatus(instanceId) {
    const maxAttempts = 240;
    let attempts = 0;

    const poll = async () => {
        try {
            const res = await fetch('/api/etl/status/' + instanceId);
            const status = await res.json();

            if (status.status === 'Completed') {
                const output = status.output ? JSON.parse(status.output) : null;
                if (output) {
                    showMessage(`All jobs completed! ${output.SuccessfulJobs}/${output.TotalJobs} succeeded, ${output.TotalProductsSaved} products saved.`, false);
                } else {
                    showMessage('All jobs completed successfully!', false);
                }
                metricsData = null;
                setTimeout(() => location.reload(), 2000);
                return;
            } else if (status.status === 'Failed') {
                showMessage('Jobs failed: ' + (status.output || 'Unknown error'), true);
                return;
            } else if (status.status === 'Terminated') {
                showMessage('Jobs were terminated', true);
                return;
            }

            attempts++;
            if (attempts >= maxAttempts) {
                showMessage('Jobs are still running. Check status at: /api/etl/status/' + instanceId, false);
                return;
            }

            showMessage(`Jobs running... (${status.status}) - ${attempts * 5}s elapsed`, false);
            setTimeout(poll, 5000);
        } catch (e) {
            showMessage('Error polling status: ' + e.message, true);
        }
    };

    await poll();
}
