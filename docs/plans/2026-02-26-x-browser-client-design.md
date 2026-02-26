# X Browser Client Design

**Date:** 2026-02-26
**Status:** Approved

## Problem

X's API blocked programmatic replies on Feb 24, 2026 (all tiers except Enterprise). The existing `twitter-api-v2` scripts can post tweets, follow accounts, upload media — but cannot reply to other users' tweets. Browser-based replies still work.

## Solution

A hybrid Node.js/TypeScript library at `scripts/x-browser/` that:
- Routes **replies** through Playwright (Firefox with real cookies)
- Routes **everything else** through `twitter-api-v2` (API)
- Exposes a single `XClient` class as the unified interface

## Architecture

```
XClient
├── .reply(tweetId, text)     → BrowserSession (Playwright)
├── .postTweet(text, media?)  → ApiClient (twitter-api-v2)
├── .postThread(tweets[])     → ApiClient
├── .uploadMedia(filePath)    → ApiClient
├── .follow(userId)           → ApiClient
├── .unfollow(userId)         → ApiClient
├── .likeTweet(tweetId)       → ApiClient
├── .retweet(tweetId)         → ApiClient
├── .fetchTweet(tweetId)      → ApiClient
├── .getMyTweets(count?)      → ApiClient
└── .close()                  → cleanup browser
```

## Components

### XClient (`index.ts`)
Main class. Constructed with path to `x-config.json`. Lazily initializes BrowserSession only when `reply()` is called. All other methods delegate to ApiClient.

### BrowserSession (`browser.ts`)
Manages a persistent Playwright Firefox context.
- Reads cookies from Firefox's `cookies.sqlite` for `.x.com` / `.twitter.com`
- Injects cookies into persistent browser context
- Navigates to tweet URL, composes reply via DOM interaction
- Screenshots on failure for debugging
- Returns `{ id, text, url }` on success

### ApiClient (`api.ts`)
Thin wrapper around `twitter-api-v2`. Loads credentials from `x-config.json`. Exposes typed methods for all API-capable actions.

### Cookie Extraction (`cookies.ts`)
Reads Firefox's `cookies.sqlite` using `better-sqlite3`. Filters for `.x.com` / `.twitter.com` domains. Converts to Playwright cookie format. Firefox profile path: `<FIREFOX_PROFILE>/`.

### DOM Selectors (`selectors.ts`)
Centralised selectors for X.com's UI elements (reply button, text area, submit button). Isolated so they're easy to update when X changes their DOM.

## Browser Reply Flow

1. Navigate to `https://x.com/user/status/{tweetId}`
2. Wait for tweet content to render
3. Click reply text area
4. Type reply text
5. Click reply submit button
6. Wait for confirmation (reply appears in thread)
7. Extract posted reply tweet URL/ID
8. Return result

## Cookie Strategy

Firefox stores cookies in `cookies.sqlite` (SQLite3). The library reads cookies at startup, injects into Playwright context. If cookies expire (detected by login redirect), the user browses X in Firefox to refresh, then restarts.

## Dependencies

- `playwright` — browser automation
- `better-sqlite3` — read Firefox cookie DB
- `twitter-api-v2` — API client (already in parent package.json)
- `typescript` + `tsx` — dev/build

## File Structure

```
scripts/x-browser/
├── src/
│   ├── index.ts        ← XClient class
│   ├── browser.ts      ← BrowserSession
│   ├── api.ts          ← ApiClient wrapper
│   ├── cookies.ts      ← Firefox cookie extraction
│   └── selectors.ts    ← X.com DOM selectors
├── tests/
│   ├── cookies.test.ts
│   ├── api.test.ts
│   ├── browser.test.ts
│   └── client.test.ts
├── package.json
└── tsconfig.json
```

## Out of Scope

- DMs, Spaces, Lists, Bookmarks
- Multi-account support
- Headless evasion (real Firefox profile is sufficient)
- Quote tweets via browser (test API first, only add browser route if blocked)
