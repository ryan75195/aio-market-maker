# Market Maker — Marketing Landing Page Design

**Date:** 2026-02-22
**Status:** Approved

## Summary

Single-page marketing/waitlist landing page for Market Maker, an eBay arbitrage tool that helps resellers find underpriced listings by comparing active listings against sold comparables using a proprietary ML model.

## Decisions

| Decision | Choice | Rationale |
|----------|--------|-----------|
| Target audience | eBay resellers / flippers | Core user base for arbitrage tools |
| Goal | Waitlist / early access signups | Pre-launch, collect interested users |
| Scope | Single landing page | Ship fast, validate interest |
| Tech stack | Astro | Static-first, zero JS by default, great for landing pages |
| Brand name | Market Maker | Matches existing project, implies market advantage |
| Approach | Product-led hero | Show the app with real profit numbers — resellers are results-oriented |

## Page Structure

Six sections, single scroll:

### 1. Navigation Bar
- Logo: "Market Maker"
- Tagline (optional, short)
- "Join Waitlist" button (scrolls to waitlist form)

### 2. Hero
- **Layout:** Headline + subhead on the left, app screenshot on the right
- **Screenshot:** Opportunities view showing the profit column with real numbers
- **Headline:** "Find Underpriced eBay Listings Before Anyone Else"
- **Subhead:** "Market Maker scans eBay, analyzes sold comparables with AI, and surfaces profit opportunities — so you can flip smarter, not harder."
- **CTA:** "Join the Waitlist" button

### 3. Feature Cards (4 cards)

| # | Title | Description |
|---|-------|-------------|
| 1 | Smart Opportunity Detection | Automatically compares active listings against sold comps to calculate real profit potential — fees included. |
| 2 | Variant-Level Price Matching | Our proprietary ML model matches listings at the variant level — not just by search keyword. A "PS5 Disc Edition" won't be priced against a "PS5 Digital" like other tools do. |
| 3 | Automated Scraping | Set up search jobs, hit "Start Scrape," and let Market Maker crawl eBay for active and sold listings across all your product categories. |
| 4 | Real-Time Dashboard | Track listings growth, profit by condition, days-to-sell estimates, and scrape progress — all in one dark-themed command center. |

**Card 2 is the key differentiator** — variant-level matching via the custom ML model is what separates Market Maker from competitors that only do keyword-level comparison.

### 4. How It Works (3 steps)

**Step 1: Set Up Your Jobs**
"Tell Market Maker what products you flip. Create search jobs like 'PS5 console' or 'AirPods Pro' and organize them into categories."

**Step 2: Scan the Market**
"Hit Start Scrape. Market Maker finds active and sold listings on eBay, then uses ML to match each listing to its true variant-level comparables."

**Step 3: Find Your Edge**
"Browse opportunities sorted by profit. See exactly what comparable items sold for, how many comps support the price, and estimated days to sell."

### 5. Waitlist CTA
- Email input + "Join the Waitlist" button
- Social proof counter: "Join X resellers on the waitlist"
- Backend: simple email collection (Buttondown, ConvertKit, or Google Sheets webhook)

### 6. Footer
- Copyright
- Contact link (optional)

## Visual Design

| Element | Value |
|---------|-------|
| Theme | Dark (matches the app) |
| Background | Dark grays (#1e1e1e, #252526) |
| Text | Light (#e0e0e0) |
| Accent | Blue (#0e639c) from the app |
| Photography | None — app screenshots and icons only |
| Tone | Professional but not corporate — speaks to hustlers, not enterprises |
| Typography | System fonts or clean sans-serif (Inter, etc.) |

## Tech Stack

- **Framework:** Astro (static output, zero JS by default)
- **Styling:** Tailwind CSS or plain CSS
- **Deployment:** GitHub Pages, Netlify, or Vercel (all free tier)
- **Email collection:** TBD (ConvertKit, Buttondown, or simple API endpoint)

## Assets Needed

- Screenshot of the Opportunities view (with realistic data showing profit)
- Screenshot of the Dashboard/Overview
- Optional: screenshot of Listing Detail with comps
- Icons for the 4 feature cards (can use Lucide, Heroicons, or similar)
