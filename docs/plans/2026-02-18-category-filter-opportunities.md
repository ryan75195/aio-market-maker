# Category Filter for Opportunities — Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Add a category filter dropdown to the opportunities view that filters listings by product category, with the job filter scoped to show only jobs within selected categories.

**Architecture:** Client-side only. The frontend resolves selected categories to job IDs using already-loaded `jobs[]` data and passes them to the existing `/api/listings/active?jobIds=` parameter. No backend changes.

**Tech Stack:** Vue.js (Options API, CDN), vanilla HTML/CSS

---

### Task 1: Add Category Filter State and Computed Properties

**Files:**
- Modify: `AIOMarketMaker.Desktop/electron/src/app.js:15` (add state)
- Modify: `AIOMarketMaker.Desktop/electron/src/app.js:104` (add computed properties)

**Step 1: Add state variables**

In `data()` after line 15 (`opportunityJobFilter: [],`), add:

```javascript
opportunityCategoryFilter: [],
showCategoryDropdown: false,
```

And after line 85 (`showJobDropdown: false,`), the `showCategoryDropdown` is already in data above so no duplicate needed.

**Step 2: Add computed properties**

After the `jobFilterLabel` computed property (line 114), add:

```javascript
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

filteredJobs() {
  if (this.opportunityCategoryFilter.length === 0) {
    return this.jobs;
  }
  const ids = this.filteredJobIds;
  if (!ids) { return this.jobs; }
  return this.jobs.filter(j => ids.includes(j.id));
},
```

**Step 3: Verify app loads without errors**

Run the Desktop app, navigate to Opportunities. The view should load as before with no errors in the console.

**Step 4: Commit**

```bash
git add AIOMarketMaker/AIOMarketMaker.Desktop/electron/src/app.js
git commit -m "feat: add category filter state and computed properties for opportunities"
```

---

### Task 2: Add Category Filter Methods

**Files:**
- Modify: `AIOMarketMaker.Desktop/electron/src/app.js:775-794` (add methods near existing filter methods)

**Step 1: Add category filter methods**

After `clearJobFilter()` (line 794), add:

```javascript
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
```

**Step 2: Update `loadOpportunities()` to use category filter**

In `loadOpportunities()` (line 734), replace the job filter block:

```javascript
// OLD (line 734-736):
if (this.opportunityJobFilter.length > 0) {
  params.set('jobIds', this.opportunityJobFilter.join(','));
}

// NEW:
let effectiveJobIds = [];
if (this.opportunityJobFilter.length > 0) {
  effectiveJobIds = this.opportunityJobFilter;
} else if (this.filteredJobIds) {
  effectiveJobIds = this.filteredJobIds;
}
if (effectiveJobIds.length > 0) {
  params.set('jobIds', effectiveJobIds.join(','));
}
```

**Step 3: Verify the category filter logic works**

Open browser console, manually set `app.opportunityCategoryFilter = [1]` (use a real category ID). The opportunities table should refresh and only show listings from jobs in that category.

**Step 4: Commit**

```bash
git add AIOMarketMaker/AIOMarketMaker.Desktop/electron/src/app.js
git commit -m "feat: add category filter methods and integrate with loadOpportunities"
```

---

### Task 3: Add Category Filter Dropdown to HTML

**Files:**
- Modify: `AIOMarketMaker.Desktop/electron/src/index.html:106-120` (add category dropdown, update job dropdown)

**Step 1: Add category dropdown before the job filter**

Replace the block from line 106-120 with:

```html
          <!-- Category Filter Dropdown -->
          <div v-if="categories.length > 0" class="filter-dropdown-wrapper">
            <button class="btn filter-dropdown-btn" @click="showCategoryDropdown = !showCategoryDropdown">
              {{ categoryFilterLabel }} <span class="dropdown-caret">&#9660;</span>
            </button>
            <div v-if="showCategoryDropdown" class="dropdown-backdrop" @click="showCategoryDropdown = false"></div>
            <div v-if="showCategoryDropdown" class="dropdown-menu">
              <label class="dropdown-item" @click.prevent="clearCategoryFilter">
                <input type="checkbox" :checked="opportunityCategoryFilter.length === 0" readonly> All
              </label>
              <label v-for="cat in categories" :key="cat.id" class="dropdown-item" @click.prevent="toggleCategoryFilter(cat.id)">
                <input type="checkbox" :checked="isCategoryFiltered(cat.id)" readonly> {{ cat.name }}
              </label>
              <label class="dropdown-item" @click.prevent="toggleCategoryFilter(-1)">
                <input type="checkbox" :checked="isCategoryFiltered(-1)" readonly> Uncategorised
              </label>
            </div>
          </div>

          <!-- Job Filter Dropdown (scoped to selected categories) -->
          <div v-if="filteredJobs.length > 0" class="filter-dropdown-wrapper">
            <button class="btn filter-dropdown-btn" @click="showJobDropdown = !showJobDropdown">
              {{ jobFilterLabel }} <span class="dropdown-caret">&#9660;</span>
            </button>
            <div v-if="showJobDropdown" class="dropdown-backdrop" @click="showJobDropdown = false"></div>
            <div v-if="showJobDropdown" class="dropdown-menu">
              <label class="dropdown-item" @click.prevent="clearJobFilter">
                <input type="checkbox" :checked="opportunityJobFilter.length === 0" readonly> All
              </label>
              <label v-for="job in filteredJobs" :key="job.id" class="dropdown-item" @click.prevent="toggleJobFilter(job.id)">
                <input type="checkbox" :checked="isJobFiltered(job.id)" readonly> {{ job.searchTerm }}
              </label>
            </div>
          </div>
```

**Step 2: Ensure categories are loaded on mount**

Check that `loadCategories()` is called during startup. Look at the `mounted()` hook. If categories aren't loaded there, add `await this.loadCategories();` after `await this.loadJobs();` (around line 284).

**Step 3: Verify the full UI works**

1. Open the Desktop app → Opportunities view
2. Verify the Category dropdown appears with all categories + "Uncategorised"
3. Select a category → job dropdown should only show jobs in that category
4. Select "Uncategorised" → should show jobs with no categories assigned
5. Clear category filter → all jobs visible again
6. Combine: select a category, then filter to a specific job within it

**Step 4: Commit**

```bash
git add AIOMarketMaker/AIOMarketMaker.Desktop/electron/src/index.html
git commit -m "feat: add category filter dropdown to opportunities view"
```

---

### Task 4: Close dropdowns when the other opens

**Files:**
- Modify: `AIOMarketMaker.Desktop/electron/src/app.js` (update toggle methods)

**Step 1: Close the other dropdown when one opens**

In the category dropdown button click, update the HTML `@click` to also close the job dropdown, and vice versa. The simplest approach: update the button clicks in `index.html`:

Category button:
```html
@click="showCategoryDropdown = !showCategoryDropdown; showJobDropdown = false"
```

Job button:
```html
@click="showJobDropdown = !showJobDropdown; showCategoryDropdown = false"
```

**Step 2: Verify**

Click category dropdown → should close job dropdown if open. Click job dropdown → should close category dropdown if open.

**Step 3: Commit**

```bash
git add AIOMarketMaker/AIOMarketMaker.Desktop/electron/src/index.html
git commit -m "feat: close other dropdown when opening category or job filter"
```
