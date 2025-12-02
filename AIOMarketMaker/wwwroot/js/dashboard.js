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
        if (!res.ok) {
            throw new Error('Failed to fetch metrics: ' + res.status);
        }
        metricsData = await res.json();
        console.log('Metrics loaded:', metricsData);
        renderDashboardStats();
        renderCharts();
    } catch (e) {
        console.error('Failed to load metrics:', e);
        document.getElementById('dashboardStats').innerHTML = `
            <div class="stat-card" style="background: #fee2e2;">
                <div class="value" style="color: #dc2626; font-size: 1em;">Error loading data</div>
                <div class="label">${e.message}</div>
            </div>
        `;
    }
}

function renderDashboardStats() {
    const m = metricsData;
    const s = m.summary;
    const totalDeals = m.arbitrageByJob ? m.arbitrageByJob.reduce((sum, j) => sum + j.dealsCount, 0) : 0;

    document.getElementById('dashboardStats').innerHTML = `
        <div class="stat-card">
            <div class="value">${s.totalProducts}</div>
            <div class="label">Products Tracked</div>
        </div>
        <div class="stat-card success">
            <div class="value">${s.soldProducts}</div>
            <div class="label">Sold (Comps)</div>
        </div>
        <div class="stat-card">
            <div class="value">${s.activeProducts}</div>
            <div class="label">Active Products</div>
        </div>
        <div class="stat-card warning">
            <div class="value">${s.medianSoldPrice.toFixed(2)}</div>
            <div class="label">Median Sold Price</div>
        </div>
        <div class="stat-card success">
            <div class="value">${s.sellThroughRate7d}%</div>
            <div class="label">Sell-Through (7d)</div>
        </div>
        <div class="stat-card" style="background: #fef3c7;">
            <div class="value" style="color: #d97706;">${totalDeals}</div>
            <div class="label">Potential Deals</div>
        </div>
    `;

    // Render best deals table
    renderDealsTable();

    // Render arbitrage by category table
    renderArbitrageTable();

    // Populate product name filter dropdown
    populateProductNameFilter();
}

function renderDealsTable() {
    const deals = metricsData.bestDeals;
    const tbody = document.getElementById('dealsBody');

    if (!deals || deals.length === 0) {
        tbody.innerHTML = '<tr><td colspan="6" style="text-align: center; padding: 20px;">No flip opportunities found (need active products priced 20%+ below median sold)</td></tr>';
        return;
    }

    tbody.innerHTML = deals.map(d => {
        const displayName = d.productName || d.title || '(no title)';
        const title = displayName.length > 40 ? displayName.substring(0, 37) + '...' : displayName;
        const profitClass = d.profitPercent >= 50 ? 'style="color: #16a34a; font-weight: bold;"' : '';
        return `<tr>
            <td class="title-cell"><a href="${d.url || '#'}" target="_blank" title="${(d.title || '').replace(/"/g, '&quot;')}">${title}</a></td>
            <td class="price">${d.price?.toFixed(2) || '-'}</td>
            <td class="price">${d.medianSoldPrice.toFixed(2)}</td>
            <td class="price" style="color: #16a34a;">+${d.potentialProfit.toFixed(2)}</td>
            <td ${profitClass}>${d.profitPercent.toFixed(0)}%</td>
            <td>${d.category || '-'}</td>
        </tr>`;
    }).join('');
}

function renderArbitrageTable() {
    const arb = metricsData.arbitrageByJob;
    const tbody = document.getElementById('arbitrageBody');

    if (!arb || arb.length === 0) {
        tbody.innerHTML = '<tr><td colspan="9" style="text-align: center; padding: 20px;">No data available</td></tr>';
        return;
    }

    tbody.innerHTML = arb.map(j => {
        const spreadClass = j.spreadPercent >= 30 ? 'style="color: #16a34a; font-weight: bold;"' : j.spreadPercent >= 15 ? 'style="color: #d97706;"' : '';
        const dealsClass = j.dealsCount > 0 ? 'style="color: #16a34a; font-weight: bold;"' : '';
        return `<tr>
            <td><strong>${j.searchTerm}</strong></td>
            <td>${j.soldCount}</td>
            <td>${j.activeCount}</td>
            <td class="price">${j.avgSoldPrice.toFixed(2)}</td>
            <td class="price">${j.medianSoldPrice.toFixed(2)}</td>
            <td class="price">${j.minActivePrice.toFixed(2)}</td>
            <td class="price" ${spreadClass}>${j.priceSpread.toFixed(2)}</td>
            <td ${spreadClass}>${j.spreadPercent.toFixed(0)}%</td>
            <td ${dealsClass}>${j.dealsCount}</td>
        </tr>`;
    }).join('');
}

function renderCharts() {
    const m = metricsData;

    // Destroy existing charts
    Object.values(charts).forEach(c => c.destroy());
    charts = {};

    // Price Distribution - Sold vs Active comparison
    charts.price = new Chart(document.getElementById('priceChart'), {
        type: 'bar',
        data: {
            labels: m.priceDistribution.map(d => d.range),
            datasets: [
                {
                    label: 'Sold',
                    data: m.priceDistribution.map(d => d.sold),
                    backgroundColor: '#16a34a'
                },
                {
                    label: 'Active',
                    data: m.priceDistribution.map(d => d.active),
                    backgroundColor: '#3b82f6'
                }
            ]
        },
        options: {
            responsive: true,
            maintainAspectRatio: false,
            plugins: { legend: { display: true, position: 'top' } }
        }
    });

    // Sales Velocity - Items sold per day
    charts.sales = new Chart(document.getElementById('salesChart'), {
        type: 'line',
        data: {
            labels: m.salesByDay.map(d => d.date.substring(5)),
            datasets: [{
                label: 'Items Sold',
                data: m.salesByDay.map(d => d.count),
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

    // Profit Spread by Category
    const spreadColors = m.arbitrageByJob.map(j =>
        j.spreadPercent >= 30 ? '#16a34a' : j.spreadPercent >= 15 ? '#f59e0b' : '#ef4444'
    );
    charts.spread = new Chart(document.getElementById('spreadChart'), {
        type: 'bar',
        data: {
            labels: m.arbitrageByJob.map(d => d.searchTerm.substring(0, 15)),
            datasets: [{
                label: 'Spread %',
                data: m.arbitrageByJob.map(d => d.spreadPercent),
                backgroundColor: spreadColors
            }]
        },
        options: {
            responsive: true,
            maintainAspectRatio: false,
            indexAxis: 'y',
            plugins: { legend: { display: false } },
            scales: {
                x: {
                    beginAtZero: true,
                    title: { display: true, text: 'Profit Margin %' }
                }
            }
        }
    });

    // Daily Volume
    charts.volume = new Chart(document.getElementById('volumeChart'), {
        type: 'bar',
        data: {
            labels: m.salesByDay.map(d => d.date.substring(5)),
            datasets: [{
                label: 'Daily Volume',
                data: m.salesByDay.map(d => d.volume),
                backgroundColor: '#8b5cf6'
            }]
        },
        options: {
            responsive: true,
            maintainAspectRatio: false,
            plugins: { legend: { display: false } },
            scales: {
                y: {
                    beginAtZero: true,
                    title: { display: true, text: 'Volume ($)' }
                }
            }
        }
    });
}

// Products tab
async function loadProducts(page) {
    currentPage = page;
    const productName = document.getElementById('filterProductName').value;
    const category = document.getElementById('filterCategory').value;
    const status = document.getElementById('filterStatus').value;
    const search = document.getElementById('filterSearch').value;
    const pageSize = document.getElementById('pageSize').value;

    const params = new URLSearchParams({ page, pageSize });
    if (productName) params.append('productName', productName);
    if (category) params.append('category', category);
    if (status) params.append('status', status);
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

// Load product names based on category filter
async function loadProductNames(category = null) {
    try {
        const params = new URLSearchParams();
        if (category) params.append('category', category);

        const res = await fetch('/api/products/names?' + params);
        if (!res.ok) {
            console.error('Failed to fetch product names');
            return;
        }

        const productNames = await res.json();
        const select = document.getElementById('filterProductName');
        const currentValue = select.value;

        // Clear existing options except the first "All Products"
        select.innerHTML = '<option value="">All Products</option>';

        // Add product names with counts
        productNames.forEach(p => {
            const option = document.createElement('option');
            option.value = p.productName;
            option.textContent = `${p.productName} (${p.count})`;
            select.appendChild(option);
        });

        // Restore previous selection if it still exists in the filtered list
        if (currentValue && productNames.some(p => p.productName === currentValue)) {
            select.value = currentValue;
        }
    } catch (e) {
        console.error('Error loading product names:', e);
    }
}

// Called when category filter changes - update product names and reload products
async function onCategoryChange() {
    const category = document.getElementById('filterCategory').value;
    await loadProductNames(category || null);
    loadProducts(1);
}

// Populate product name dropdown from metrics (initial load)
function populateProductNameFilter() {
    if (!metricsData || !metricsData.productNameBreakdown) return;

    const select = document.getElementById('filterProductName');
    const currentValue = select.value;

    // Clear existing options except the first "All Products"
    select.innerHTML = '<option value="">All Products</option>';

    // Add product names sorted by count
    metricsData.productNameBreakdown.forEach(p => {
        const option = document.createElement('option');
        option.value = p.productName;
        option.textContent = `${p.productName} (${p.count})`;
        select.appendChild(option);
    });

    // Restore previous selection if it still exists
    if (currentValue) {
        select.value = currentValue;
    }
}

function renderProductsTable(products) {
    const tbody = document.getElementById('productsBody');
    if (products.length === 0) {
        tbody.innerHTML = '<tr><td colspan="9" style="text-align: center; padding: 40px;">No products found</td></tr>';
        return;
    }

    tbody.innerHTML = products.map(p => {
        const statusClass = p.listingStatus === 'Sold' ? 'status-sold' : 'status-active';
        const displayName = p.productName || p.title || '(no title)';
        const title = displayName.length > 50 ? displayName.substring(0, 47) + '...' : displayName;
        const price = p.price ? p.price.toFixed(2) + ' ' + (p.currency || '') : '-';
        const endDate = p.endDateUtc ? p.endDateUtc.substring(0, 10) : '-';

        return `<tr>
            <td>${p.id}</td>
            <td><a href="${p.url || '#'}" target="_blank">${p.ebayListingId || '-'}</a></td>
            <td class="title-cell" title="${(p.title || '').replace(/"/g, '&quot;')}">${title}</td>
            <td class="price">${price}</td>
            <td class="${statusClass}">${p.listingStatus || '-'}</td>
            <td>${p.condition || '-'}</td>
            <td>${p.category || '-'}</td>
            <td>${endDate}</td>
            <td><button class="btn btn-primary btn-sm" onclick="viewProduct(${p.id})">View</button></td>
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

// Edit job functions
async function editJob(id) {
    try {
        const res = await fetch('/api/jobs');
        const jobs = await res.json();
        const job = jobs.find(j => j.Id === id);

        if (!job) {
            showMessage('Job not found', true);
            return;
        }

        // Populate the edit form (API returns PascalCase properties)
        document.getElementById('editJobId').value = job.Id;
        document.getElementById('editSearchTerm').value = job.SearchTerm || '';
        document.getElementById('editSearchType').value = job.SearchType || 'SOLD';
        document.getElementById('editBuyingFormat').value = job.BuyingFormat || 'BUY_NOW';
        document.getElementById('editCondition').value = job.Condition || 'USED';
        document.getElementById('editFrequencyMinutes').value = job.FrequencyMinutes || 60;
        document.getElementById('editLookbackDays').value = job.LookbackDays || '';
        document.getElementById('editItemLimit').value = job.ItemLimit || '';
        document.getElementById('editIsEnabled').checked = job.IsEnabled;

        // Show the modal
        const modal = document.getElementById('editModal');
        modal.style.display = 'flex';
        modal.classList.add('show');
    } catch (e) {
        showMessage('Error loading job: ' + e.message, true);
    }
}

function closeEditModal() {
    const modal = document.getElementById('editModal');
    modal.style.display = 'none';
    modal.classList.remove('show');
}

async function saveJob() {
    const id = document.getElementById('editJobId').value;
    const searchTerm = document.getElementById('editSearchTerm').value.trim();

    if (!searchTerm) {
        showMessage('Search term is required', true);
        return;
    }

    const data = {
        searchTerm,
        searchType: document.getElementById('editSearchType').value,
        buyingFormat: document.getElementById('editBuyingFormat').value,
        condition: document.getElementById('editCondition').value,
        frequencyMinutes: parseInt(document.getElementById('editFrequencyMinutes').value) || 60,
        lookbackDays: parseInt(document.getElementById('editLookbackDays').value) || null,
        itemLimit: parseInt(document.getElementById('editItemLimit').value) || null,
        isEnabled: document.getElementById('editIsEnabled').checked
    };

    try {
        const res = await fetch('/api/jobs/' + id, {
            method: 'PUT',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify(data)
        });

        if (res.ok) {
            showMessage('Job updated successfully!', false);
            closeEditModal();
            setTimeout(() => location.reload(), 1000);
        } else {
            const err = await res.json();
            showMessage(err.error || 'Failed to update job', true);
        }
    } catch (e) {
        showMessage('Error: ' + e.message, true);
    }
}

// Close modal when clicking outside
document.addEventListener('click', (e) => {
    const editModal = document.getElementById('editModal');
    const productModal = document.getElementById('productModal');
    if (e.target === editModal) {
        closeEditModal();
    }
    if (e.target === productModal) {
        closeProductModal();
    }
});

// Close modal on Escape key
document.addEventListener('keydown', (e) => {
    if (e.key === 'Escape') {
        closeEditModal();
        closeProductModal();
    }
});

// Product details modal functions
async function viewProduct(id) {
    const modal = document.getElementById('productModal');
    const body = document.getElementById('productModalBody');

    // Show modal with loading state
    modal.style.display = 'flex';
    modal.classList.add('show');
    body.innerHTML = '<div style="text-align: center; padding: 40px;">Loading...</div>';

    try {
        const res = await fetch('/api/products/' + id + '/details');
        if (!res.ok) {
            throw new Error('Failed to load product details');
        }
        const data = await res.json();
        renderProductDetails(data);
    } catch (e) {
        body.innerHTML = `<div style="text-align: center; padding: 40px; color: #dc2626;">Error: ${e.message}</div>`;
    }
}

function closeProductModal() {
    const modal = document.getElementById('productModal');
    modal.style.display = 'none';
    modal.classList.remove('show');
}

function renderProductDetails(data) {
    const p = data.product;
    const history = data.history || [];

    const formatDate = (dateStr) => {
        if (!dateStr) return '-';
        const d = new Date(dateStr);
        return d.toLocaleString();
    };

    const formatPrice = (price, currency) => {
        if (price == null) return '-';
        return price.toFixed(2) + ' ' + (currency || '');
    };

    let html = `
        <div class="product-details">
            <div class="detail-section">
                <h4>Listing Information</h4>
                <div class="detail-row"><span class="detail-label">Listing ID:</span><span class="detail-value"><a href="${p.url || '#'}" target="_blank">${p.listingId}</a></span></div>
                <div class="detail-row"><span class="detail-label">Title:</span><span class="detail-value">${p.title || '-'}</span></div>
                <div class="detail-row"><span class="detail-label">Price:</span><span class="detail-value">${formatPrice(p.price, p.currency)}</span></div>
                <div class="detail-row"><span class="detail-label">Shipping:</span><span class="detail-value">${formatPrice(p.shippingCost, p.currency)}</span></div>
                <div class="detail-row"><span class="detail-label">Status:</span><span class="detail-value ${p.listingStatus === 'Sold' ? 'status-sold' : 'status-active'}">${p.listingStatus || '-'}</span></div>
                <div class="detail-row"><span class="detail-label">Condition:</span><span class="detail-value">${p.condition || '-'}</span></div>
                <div class="detail-row"><span class="detail-label">Format:</span><span class="detail-value">${p.purchaseFormat || '-'}</span></div>
            </div>
            <div class="detail-section">
                <h4>Tracking</h4>
                <div class="detail-row"><span class="detail-label">Job:</span><span class="detail-value">${p.job ? p.job.searchTerm : '-'}</span></div>
                <div class="detail-row"><span class="detail-label">Location:</span><span class="detail-value">${p.location || '-'}</span></div>
                <div class="detail-row"><span class="detail-label">End Date:</span><span class="detail-value">${formatDate(p.endDateUtc)}</span></div>
                <div class="detail-row"><span class="detail-label">First Seen:</span><span class="detail-value">${formatDate(p.createdUtc)}</span></div>
                <div class="detail-row"><span class="detail-label">Last Updated:</span><span class="detail-value">${formatDate(p.updatedUtc)}</span></div>
            </div>
        </div>
    `;

    // Add status history section
    html += `
        <div class="history-section">
            <h4>Status History</h4>
            <table class="history-table">
                <thead>
                    <tr>
                        <th>Status</th>
                        <th>Price</th>
                        <th>Sold Date</th>
                        <th>Recorded</th>
                        <th>Source</th>
                    </tr>
                </thead>
                <tbody>
    `;

    if (history.length === 0) {
        html += '<tr><td colspan="5" style="text-align: center; padding: 20px;">No history records</td></tr>';
    } else {
        history.forEach(h => {
            const statusClass = h.listingStatus === 'Sold' ? 'status-sold' : 'status-active';
            html += `
                <tr>
                    <td class="${statusClass}">${h.listingStatus}</td>
                    <td class="price">${h.price != null ? h.price.toFixed(2) : '-'}</td>
                    <td>${formatDate(h.soldDateUtc)}</td>
                    <td>${formatDate(h.recordedUtc)}</td>
                    <td>${h.source || '-'}</td>
                </tr>
            `;
        });
    }

    html += '</tbody></table></div>';

    document.getElementById('productModalBody').innerHTML = html;
}

// Status refresh functions
async function refreshAllStatuses() {
    if (!confirm('Refresh status for all active listings? This will check each listing URL for status changes.')) return;

    showMessage('Starting status refresh...', false);

    try {
        const res = await fetch('/api/status/refresh', { method: 'POST' });
        const result = await res.json();

        if (!res.ok) {
            showMessage('Failed to start status refresh: ' + (result.error || 'Unknown error'), true);
            return;
        }

        if (result.message) {
            showMessage(result.message, false);
            return;
        }

        showMessage(`Status refresh started for ${result.activeListings} listings. Instance ID: ${result.instanceId}. Polling...`, false);
        await pollStatusRefresh(result.instanceId);
    } catch (e) {
        showMessage('Error: ' + e.message, true);
    }
}

async function refreshJobStatuses(jobId) {
    if (!confirm('Refresh status for active listings in this job?')) return;

    showMessage('Starting status refresh for job...', false);

    try {
        const res = await fetch('/api/status/refresh/' + jobId, { method: 'POST' });
        const result = await res.json();

        if (!res.ok) {
            showMessage('Failed to start status refresh: ' + (result.error || 'Unknown error'), true);
            return;
        }

        if (result.message) {
            showMessage(result.message, false);
            return;
        }

        showMessage(`Status refresh started for ${result.activeListings} listings. Polling...`, false);
        await pollStatusRefresh(result.instanceId);
    } catch (e) {
        showMessage('Error: ' + e.message, true);
    }
}

async function pollStatusRefresh(instanceId) {
    const maxAttempts = 120;
    let attempts = 0;

    const poll = async () => {
        try {
            const res = await fetch('/api/status/refresh/status/' + instanceId);
            const status = await res.json();

            if (status.status === 'Completed') {
                const output = status.output ? JSON.parse(status.output) : null;
                if (output) {
                    showMessage(`Status refresh complete! Checked ${output.Checked} listings, ${output.Updated} changed.`, false);
                } else {
                    showMessage('Status refresh completed successfully!', false);
                }
                metricsData = null; // Force refresh
                setTimeout(() => {
                    loadProducts(currentPage);
                    loadDashboardData();
                }, 1000);
                return;
            } else if (status.status === 'Failed') {
                showMessage('Status refresh failed: ' + (status.output || 'Unknown error'), true);
                return;
            } else if (status.status === 'Terminated') {
                showMessage('Status refresh was terminated', true);
                return;
            }

            attempts++;
            if (attempts >= maxAttempts) {
                showMessage('Status refresh is still running. Check status at: /api/status/refresh/status/' + instanceId, false);
                return;
            }

            showMessage(`Status refresh running... (${status.status}) - ${attempts * 5}s elapsed`, false);
            setTimeout(poll, 5000);
        } catch (e) {
            showMessage('Error polling status: ' + e.message, true);
        }
    };

    await poll();
}
