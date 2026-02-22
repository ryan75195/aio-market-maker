# Market Maker Landing Page — Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Build a single-page marketing/waitlist landing page for Market Maker using Astro, with dark theme, product-led hero, and email signup.

**Architecture:** Static Astro site in a new `website/` directory at the repo root. Tailwind CSS for styling. No backend — waitlist form submits to a Netlify Forms endpoint (zero-config, free tier). Deployed to Netlify via `netlify deploy`.

**Tech Stack:** Astro 5, Tailwind CSS 4, Lucide icons (via astro-icon), Netlify Forms for email collection.

**Design doc:** `AIOMarketMaker/docs/plans/2026-02-22-marketing-landing-page-design.md`

---

### Task 1: Scaffold Astro Project

**Files:**
- Create: `website/package.json`
- Create: `website/astro.config.mjs`
- Create: `website/tsconfig.json`
- Create: `website/src/pages/index.astro` (placeholder)
- Create: `website/src/layouts/Layout.astro`
- Create: `website/public/` (empty, for static assets)

**Step 1: Initialize Astro project**

```bash
cd <REPO_ROOT>
npm create astro@latest website -- --template minimal --no-install --no-git --typescript strict
```

**Step 2: Install dependencies**

```bash
cd website && npm install
npm install @astrojs/tailwind tailwindcss
```

**Step 3: Configure Tailwind in astro.config.mjs**

```js
import { defineConfig } from 'astro/config';
import tailwind from '@astrojs/tailwind';

export default defineConfig({
  integrations: [tailwind()],
});
```

**Step 4: Create base layout**

Create `src/layouts/Layout.astro` with:
- HTML boilerplate, dark background (`bg-[#1e1e1e]`), light text (`text-[#e0e0e0]`)
- Meta tags: title "Market Maker", description, viewport
- Inter font from Google Fonts CDN
- `<slot />` for page content

**Step 5: Verify dev server starts**

Run: `npm run dev`
Expected: Astro dev server starts, page loads at localhost:4321

**Step 6: Commit**

```bash
git add website/
git commit -m "feat: scaffold Astro landing page with Tailwind CSS"
```

---

### Task 2: Navigation Bar

**Files:**
- Create: `website/src/components/Navbar.astro`
- Modify: `website/src/pages/index.astro`

**Step 1: Build Navbar component**

```astro
---
// Navbar.astro
---
<nav class="fixed top-0 w-full z-50 bg-[#252526]/90 backdrop-blur border-b border-[#3c3c3c]">
  <div class="max-w-6xl mx-auto px-6 py-4 flex justify-between items-center">
    <span class="text-xl font-bold text-white">Market Maker</span>
    <a href="#waitlist"
       class="bg-[#0e639c] hover:bg-[#1177bb] text-white px-5 py-2 rounded text-sm font-medium transition-colors">
      Join Waitlist
    </a>
  </div>
</nav>
```

**Step 2: Add Navbar to index.astro**

Import and render `<Navbar />` at the top of the page inside `<Layout>`.

**Step 3: Verify in browser**

Run: `npm run dev`
Expected: Fixed navbar with "Market Maker" logo and blue "Join Waitlist" button

**Step 4: Commit**

```bash
git add website/src/
git commit -m "feat: add fixed navbar with logo and waitlist CTA"
```

---

### Task 3: Hero Section

**Files:**
- Create: `website/src/components/Hero.astro`
- Modify: `website/src/pages/index.astro`
- Add: `website/public/screenshots/opportunities.png` (placeholder — user will provide real screenshot)

**Step 1: Build Hero component**

Two-column layout:
- **Left:** Headline, subhead, CTA button
- **Right:** App screenshot (or placeholder box if no screenshot yet)

```astro
---
// Hero.astro
---
<section class="pt-28 pb-20 px-6">
  <div class="max-w-6xl mx-auto grid md:grid-cols-2 gap-12 items-center">
    <div>
      <h1 class="text-4xl md:text-5xl font-bold text-white leading-tight">
        Find Underpriced eBay Listings Before Anyone Else
      </h1>
      <p class="mt-6 text-lg text-[#a0a0a0] leading-relaxed">
        Market Maker scans eBay, analyzes sold comparables with AI, and surfaces
        profit opportunities — so you can flip smarter, not harder.
      </p>
      <a href="#waitlist"
         class="mt-8 inline-block bg-[#0e639c] hover:bg-[#1177bb] text-white px-8 py-3 rounded-lg text-lg font-medium transition-colors">
        Join the Waitlist
      </a>
    </div>
    <div class="relative">
      <div class="rounded-lg overflow-hidden border border-[#3c3c3c] shadow-2xl">
        <img src="/screenshots/opportunities.png"
             alt="Market Maker opportunities view showing profit analysis"
             class="w-full" />
      </div>
    </div>
  </div>
</section>
```

**Step 2: Create placeholder screenshot**

If no real screenshot exists yet, create a placeholder `<div>` with text "Screenshot placeholder" styled to look like a dark app window (800x500, bg-[#252526], rounded).

**Step 3: Add Hero to index.astro**

Import and render `<Hero />` below `<Navbar />`.

**Step 4: Verify in browser**

Expected: Two-column hero with headline on left, screenshot placeholder on right. CTA links to `#waitlist`.

**Step 5: Commit**

```bash
git add website/
git commit -m "feat: add hero section with headline and app screenshot"
```

---

### Task 4: Feature Cards Section

**Files:**
- Create: `website/src/components/Features.astro`
- Modify: `website/src/pages/index.astro`

**Step 1: Build Features component**

4-column grid (2-col on mobile) with icon, title, description per card. Use inline SVG icons or emoji placeholders. Data:

| Icon | Title | Description |
|------|-------|-------------|
| Target/crosshair | Smart Opportunity Detection | Automatically compares active listings against sold comps to calculate real profit potential — fees included. |
| Brain/cpu | Variant-Level Price Matching | Our proprietary ML model matches listings at the variant level — not just by search keyword. A "PS5 Disc Edition" won't be priced against a "PS5 Digital" like other tools do. |
| Zap/bolt | Automated Scraping | Set up search jobs, hit "Start Scrape," and let Market Maker crawl eBay for active and sold listings across all your product categories. |
| BarChart | Real-Time Dashboard | Track listings growth, profit by condition, days-to-sell estimates, and scrape progress — all in one dark-themed command center. |

Each card: `bg-[#252526]` background, `border border-[#3c3c3c]`, rounded, padded.

**Step 2: Add Features to index.astro**

Render `<Features />` below `<Hero />`.

**Step 3: Verify in browser**

Expected: 4 cards in a grid, responsive to 2-col on smaller screens. Card 2 (variant-level matching) should feel like the standout feature — slightly different border color or badge.

**Step 4: Commit**

```bash
git add website/src/
git commit -m "feat: add feature cards highlighting key capabilities"
```

---

### Task 5: How It Works Section

**Files:**
- Create: `website/src/components/HowItWorks.astro`
- Modify: `website/src/pages/index.astro`

**Step 1: Build HowItWorks component**

Three numbered steps in a horizontal row (stacked on mobile):

```
  1                    2                    3
Set Up Your Jobs  →  Scan the Market  →  Find Your Edge
```

Each step: large number, title, description. Connected by a subtle line or arrow between steps.

**Step 1 content:** "Tell Market Maker what products you flip. Create search jobs like 'PS5 console' or 'AirPods Pro' and organize them into categories."

**Step 2 content:** "Hit Start Scrape. Market Maker finds active and sold listings on eBay, then uses ML to match each listing to its true variant-level comparables."

**Step 3 content:** "Browse opportunities sorted by profit. See exactly what comparable items sold for, how many comps support the price, and estimated days to sell."

**Step 2: Add to index.astro**

Render `<HowItWorks />` below `<Features />`.

**Step 3: Verify in browser**

Expected: 3 steps visible with clear visual progression.

**Step 4: Commit**

```bash
git add website/src/
git commit -m "feat: add how-it-works section with 3-step flow"
```

---

### Task 6: Waitlist CTA Section

**Files:**
- Create: `website/src/components/Waitlist.astro`
- Modify: `website/src/pages/index.astro`

**Step 1: Build Waitlist component**

Centered section with `id="waitlist"` for anchor scrolling.

```astro
<section id="waitlist" class="py-20 px-6">
  <div class="max-w-xl mx-auto text-center">
    <h2 class="text-3xl font-bold text-white">Get Early Access</h2>
    <p class="mt-4 text-[#a0a0a0]">
      Be the first to know when Market Maker launches.
    </p>
    <form name="waitlist" method="POST" data-netlify="true"
          class="mt-8 flex gap-3 max-w-md mx-auto">
      <input type="hidden" name="form-name" value="waitlist" />
      <input type="email" name="email" required
             placeholder="you@example.com"
             class="flex-1 px-4 py-3 rounded-lg bg-[#2d2d2d] border border-[#3c3c3c] text-white placeholder-[#666] focus:outline-none focus:border-[#0e639c]" />
      <button type="submit"
              class="bg-[#0e639c] hover:bg-[#1177bb] text-white px-6 py-3 rounded-lg font-medium transition-colors whitespace-nowrap">
        Join Waitlist
      </button>
    </form>
  </div>
</section>
```

**Notes on form backend:**
- `data-netlify="true"` makes Netlify auto-detect and handle the form (no backend code needed)
- If not deploying to Netlify, replace with a Formspree or Google Forms action URL
- For local dev, the form will submit but won't be processed (that's fine)

**Step 2: Add to index.astro**

Render `<Waitlist />` below `<HowItWorks />`.

**Step 3: Verify in browser**

Expected: Centered email form with "Join Waitlist" button. Clicking navbar CTA scrolls to this section.

**Step 4: Commit**

```bash
git add website/src/
git commit -m "feat: add waitlist email signup section with Netlify Forms"
```

---

### Task 7: Footer

**Files:**
- Create: `website/src/components/Footer.astro`
- Modify: `website/src/pages/index.astro`

**Step 1: Build Footer component**

Minimal footer:

```astro
<footer class="border-t border-[#3c3c3c] py-8 px-6">
  <div class="max-w-6xl mx-auto text-center text-sm text-[#666]">
    &copy; 2026 Market Maker. All rights reserved.
  </div>
</footer>
```

**Step 2: Add to index.astro**

Render `<Footer />` as the last section.

**Step 3: Commit**

```bash
git add website/src/
git commit -m "feat: add footer"
```

---

### Task 8: Polish & Responsive Pass

**Files:**
- Modify: `website/src/components/*.astro` (all components)
- Modify: `website/src/layouts/Layout.astro`

**Step 1: Add smooth scroll behavior**

In Layout.astro, add `scroll-behavior: smooth` to `<html>` element via class or style.

**Step 2: Add favicon**

Create or add a simple favicon to `website/public/favicon.svg` (a simple "M" or chart icon).

**Step 3: Add meta tags for social sharing**

In Layout.astro, add Open Graph tags:
- `og:title`: "Market Maker — Find Underpriced eBay Listings"
- `og:description`: the subhead copy
- `og:image`: a screenshot or generated OG image
- `og:type`: "website"

**Step 4: Mobile responsive check**

Verify all sections work at 375px (mobile), 768px (tablet), 1280px (desktop):
- Hero stacks vertically on mobile
- Feature cards go to 1-col on mobile, 2-col on tablet
- How it works steps stack vertically on mobile
- Waitlist form stacks on very narrow screens

**Step 5: Production build**

Run: `npm run build`
Expected: Static files in `website/dist/`, no errors.

**Step 6: Commit**

```bash
git add website/
git commit -m "feat: polish responsive layout, meta tags, favicon"
```

---

### Task 9: Deploy to Netlify

**Step 1: Add netlify.toml**

Create `website/netlify.toml`:

```toml
[build]
  command = "npm run build"
  publish = "dist"
```

**Step 2: Deploy via CLI**

```bash
cd website
npx netlify-cli deploy --prod --dir=dist
```

Or connect the repo to Netlify via the web dashboard for auto-deploys.

**Step 3: Verify live site**

- All sections render correctly
- Waitlist form submits and appears in Netlify Forms dashboard
- Smooth scroll works
- Mobile layout is correct

**Step 4: Commit deploy config**

```bash
git add website/netlify.toml
git commit -m "chore: add Netlify deploy configuration"
```

---

## Assets Checklist (User Action Required)

Before or during implementation, the user needs to provide:

- [ ] Screenshot of Opportunities view (with realistic profit data visible)
- [ ] Screenshot of Dashboard/Overview (optional, for feature card or hero)
- [ ] Decision on domain name (use Netlify subdomain for now, or custom domain)
