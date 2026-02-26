# X Browser Client Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Build a hybrid X client library where replies go through Playwright (browser) and everything else goes through the twitter-api-v2 API.

**Architecture:** `XClient` class delegates to `ApiClient` (twitter-api-v2 wrapper) for all standard actions, and `BrowserSession` (Playwright Firefox with real cookies) for replies. Cookies are extracted from Firefox's `cookies.sqlite` using `better-sqlite3`.

**Tech Stack:** TypeScript, Playwright (Firefox), better-sqlite3, twitter-api-v2, vitest (testing)

---

### Task 1: Scaffold project and install dependencies

**Files:**
- Create: `scripts/x-browser/package.json`
- Create: `scripts/x-browser/tsconfig.json`

**Step 1: Create directory and package.json**

```bash
mkdir -p scripts/x-browser/src scripts/x-browser/tests
```

Write `scripts/x-browser/package.json`:
```json
{
  "name": "x-browser",
  "version": "1.0.0",
  "type": "module",
  "scripts": {
    "test": "vitest run",
    "test:watch": "vitest"
  },
  "dependencies": {
    "better-sqlite3": "^11.0.0",
    "playwright": "^1.50.0",
    "twitter-api-v2": "^1.29.0"
  },
  "devDependencies": {
    "@types/better-sqlite3": "^7.6.0",
    "typescript": "^5.7.0",
    "vitest": "^3.0.0"
  }
}
```

Write `scripts/x-browser/tsconfig.json`:
```json
{
  "compilerOptions": {
    "target": "ES2022",
    "module": "ESNext",
    "moduleResolution": "bundler",
    "strict": true,
    "esModuleInterop": true,
    "outDir": "dist",
    "rootDir": "src",
    "declaration": true,
    "allowImportingTsExtensions": true,
    "noEmit": true
  },
  "include": ["src/**/*.ts", "tests/**/*.ts"]
}
```

**Step 2: Install dependencies**

```bash
cd scripts/x-browser && npm install
```

**Step 3: Install Playwright Firefox browser**

```bash
cd scripts/x-browser && npx playwright install firefox
```

**Step 4: Commit**

```bash
git add scripts/x-browser/package.json scripts/x-browser/tsconfig.json scripts/x-browser/package-lock.json
git commit -m "feat: scaffold x-browser project with deps"
```

---

### Task 2: Cookie extraction module

**Files:**
- Create: `scripts/x-browser/src/cookies.ts`
- Create: `scripts/x-browser/tests/cookies.test.ts`

Firefox stores cookies in SQLite (`moz_cookies` table). We need to:
1. Open the DB read-only (Firefox may have it locked — copy first)
2. Query for `.x.com` and `.twitter.com` domains
3. Convert to Playwright cookie format

**Step 1: Write the failing test**

Write `scripts/x-browser/tests/cookies.test.ts`:

```typescript
import { describe, it, expect } from 'vitest';
import { extractTwitterCookies, type PlaywrightCookie } from '../src/cookies.js';
import Database from 'better-sqlite3';
import fs from 'fs';
import path from 'path';
import os from 'os';

function createTestCookieDb(): string {
  const dbPath = path.join(os.tmpdir(), `test-cookies-${Date.now()}.sqlite`);
  const db = new Database(dbPath);
  db.exec(`
    CREATE TABLE moz_cookies (
      id INTEGER PRIMARY KEY,
      name TEXT,
      value TEXT,
      host TEXT,
      path TEXT DEFAULT '/',
      expiry INTEGER DEFAULT 0,
      isSecure INTEGER DEFAULT 1,
      isHttpOnly INTEGER DEFAULT 1,
      sameSite INTEGER DEFAULT 0,
      originAttributes TEXT DEFAULT ''
    )
  `);
  const insert = db.prepare(
    'INSERT INTO moz_cookies (name, value, host, path, expiry, isSecure, isHttpOnly, sameSite) VALUES (?, ?, ?, ?, ?, ?, ?, ?)'
  );
  // X.com cookies
  insert.run('auth_token', 'abc123', '.x.com', '/', 1800000000, 1, 1, 0);
  insert.run('ct0', 'csrf456', '.x.com', '/', 1800000000, 1, 0, 2);
  // Twitter.com cookie
  insert.run('twid', 'u=12345', '.twitter.com', '/', 1800000000, 1, 1, 0);
  // Unrelated cookie — should be excluded
  insert.run('session', 'xyz', '.google.com', '/', 1800000000, 1, 1, 0);
  db.close();
  return dbPath;
}

describe('extractTwitterCookies', () => {
  it('should extract only x.com and twitter.com cookies', () => {
    const dbPath = createTestCookieDb();
    try {
      const cookies = extractTwitterCookies(dbPath);
      expect(cookies).toHaveLength(3);
      const names = cookies.map(c => c.name).sort();
      expect(names).toEqual(['auth_token', 'ct0', 'twid']);
    } finally {
      fs.unlinkSync(dbPath);
    }
  });

  it('should convert to Playwright cookie format', () => {
    const dbPath = createTestCookieDb();
    try {
      const cookies = extractTwitterCookies(dbPath);
      const authCookie = cookies.find(c => c.name === 'auth_token')!;
      expect(authCookie).toMatchObject({
        name: 'auth_token',
        value: 'abc123',
        domain: '.x.com',
        path: '/',
        secure: true,
        httpOnly: true,
        expires: 1800000000,
      });
    } finally {
      fs.unlinkSync(dbPath);
    }
  });

  it('should map sameSite values correctly', () => {
    const dbPath = createTestCookieDb();
    try {
      const cookies = extractTwitterCookies(dbPath);
      const auth = cookies.find(c => c.name === 'auth_token')!;
      const ct0 = cookies.find(c => c.name === 'ct0')!;
      expect(auth.sameSite).toBe('None');  // sameSite=0 in Firefox
      expect(ct0.sameSite).toBe('Strict'); // sameSite=2 in Firefox
    } finally {
      fs.unlinkSync(dbPath);
    }
  });

  it('should throw if DB does not exist', () => {
    expect(() => extractTwitterCookies('/nonexistent/cookies.sqlite'))
      .toThrow();
  });
});
```

**Step 2: Run test to verify it fails**

```bash
cd scripts/x-browser && npx vitest run tests/cookies.test.ts
```

Expected: FAIL — `../src/cookies.js` does not exist.

**Step 3: Write implementation**

Write `scripts/x-browser/src/cookies.ts`:

```typescript
import Database from 'better-sqlite3';
import fs from 'fs';
import path from 'path';
import os from 'os';

export interface PlaywrightCookie {
  name: string;
  value: string;
  domain: string;
  path: string;
  expires: number;
  httpOnly: boolean;
  secure: boolean;
  sameSite: 'Strict' | 'Lax' | 'None';
}

const SAME_SITE_MAP: Record<number, PlaywrightCookie['sameSite']> = {
  0: 'None',
  1: 'Lax',
  2: 'Strict',
};

const TWITTER_DOMAINS = ['.x.com', '.twitter.com'];

export function extractTwitterCookies(dbPath: string): PlaywrightCookie[] {
  // Firefox locks cookies.sqlite while running — copy to temp to avoid locking issues
  const tempPath = path.join(os.tmpdir(), `cookies-copy-${Date.now()}.sqlite`);
  fs.copyFileSync(dbPath, tempPath);

  try {
    const db = new Database(tempPath, { readonly: true });
    const placeholders = TWITTER_DOMAINS.map(() => '?').join(', ');
    const rows = db.prepare(
      `SELECT name, value, host, path, expiry, isSecure, isHttpOnly, sameSite
       FROM moz_cookies
       WHERE host IN (${placeholders})`
    ).all(...TWITTER_DOMAINS) as Array<{
      name: string;
      value: string;
      host: string;
      path: string;
      expiry: number;
      isSecure: number;
      isHttpOnly: number;
      sameSite: number;
    }>;
    db.close();

    return rows.map(row => ({
      name: row.name,
      value: row.value,
      domain: row.host,
      path: row.path,
      expires: row.expiry,
      httpOnly: row.isHttpOnly === 1,
      secure: row.isSecure === 1,
      sameSite: SAME_SITE_MAP[row.sameSite] ?? 'None',
    }));
  } finally {
    fs.unlinkSync(tempPath);
  }
}

/** Default Firefox profile cookie path on this machine */
export const DEFAULT_COOKIE_DB = '<FIREFOX_PROFILE>/cookies.sqlite';
```

**Step 4: Run tests to verify they pass**

```bash
cd scripts/x-browser && npx vitest run tests/cookies.test.ts
```

Expected: 4 tests PASS.

**Step 5: Commit**

```bash
git add scripts/x-browser/src/cookies.ts scripts/x-browser/tests/cookies.test.ts
git commit -m "feat: cookie extraction from Firefox sqlite"
```

---

### Task 3: API client wrapper

**Files:**
- Create: `scripts/x-browser/src/api.ts`
- Create: `scripts/x-browser/tests/api.test.ts`

Thin typed wrapper around `twitter-api-v2`. All methods delegate to the underlying client. Tests mock `twitter-api-v2`.

**Step 1: Write the failing test**

Write `scripts/x-browser/tests/api.test.ts`:

```typescript
import { describe, it, expect, vi, beforeEach } from 'vitest';
import { ApiClient, type XConfig } from '../src/api.js';

// Mock twitter-api-v2
vi.mock('twitter-api-v2', () => {
  const mockV2 = {
    tweet: vi.fn(),
    deleteTweet: vi.fn(),
    like: vi.fn(),
    retweet: vi.fn(),
    me: vi.fn(),
    singleTweet: vi.fn(),
    userTimeline: vi.fn(),
    follow: vi.fn(),
    unfollow: vi.fn(),
  };
  const mockV1 = {
    uploadMedia: vi.fn(),
  };
  return {
    TwitterApi: vi.fn().mockImplementation(() => ({
      v2: mockV2,
      v1: mockV1,
    })),
    __mockV2: mockV2,
    __mockV1: mockV1,
  };
});

const config: XConfig = {
  apiKey: 'key',
  apiSecret: 'secret',
  accessToken: 'token',
  accessSecret: 'asecret',
};

describe('ApiClient', () => {
  let api: ApiClient;

  beforeEach(() => {
    vi.clearAllMocks();
    api = new ApiClient(config);
  });

  it('should post a tweet', async () => {
    const { __mockV2 } = await import('twitter-api-v2') as any;
    __mockV2.tweet.mockResolvedValue({ data: { id: '123', text: 'hello' } });

    const result = await api.postTweet('hello');
    expect(__mockV2.tweet).toHaveBeenCalledWith('hello', undefined);
    expect(result).toEqual({ id: '123', text: 'hello' });
  });

  it('should post a tweet with media', async () => {
    const { __mockV2 } = await import('twitter-api-v2') as any;
    __mockV2.tweet.mockResolvedValue({ data: { id: '456', text: 'pic' } });

    await api.postTweet('pic', ['media1']);
    expect(__mockV2.tweet).toHaveBeenCalledWith('pic', { media: { media_ids: ['media1'] } });
  });

  it('should post a thread', async () => {
    const { __mockV2 } = await import('twitter-api-v2') as any;
    __mockV2.tweet
      .mockResolvedValueOnce({ data: { id: '1', text: 'first' } })
      .mockResolvedValueOnce({ data: { id: '2', text: 'second' } });

    const ids = await api.postThread(['first', 'second']);
    expect(ids).toEqual(['1', '2']);
    expect(__mockV2.tweet).toHaveBeenCalledTimes(2);
    expect(__mockV2.tweet).toHaveBeenNthCalledWith(2, 'second', { reply: { in_reply_to_tweet_id: '1' } });
  });

  it('should upload media', async () => {
    const { __mockV1 } = await import('twitter-api-v2') as any;
    __mockV1.uploadMedia.mockResolvedValue('media_789');

    const mediaId = await api.uploadMedia('/path/to/image.png');
    expect(mediaId).toBe('media_789');
  });

  it('should follow a user', async () => {
    const { __mockV2 } = await import('twitter-api-v2') as any;
    __mockV2.me.mockResolvedValue({ data: { id: 'myid' } });
    __mockV2.follow.mockResolvedValue({});

    await api.follow('target123');
    expect(__mockV2.follow).toHaveBeenCalledWith('myid', 'target123');
  });

  it('should fetch a tweet', async () => {
    const { __mockV2 } = await import('twitter-api-v2') as any;
    __mockV2.singleTweet.mockResolvedValue({
      data: { id: '999', text: 'content' },
    });

    const tweet = await api.fetchTweet('999');
    expect(tweet).toEqual({ id: '999', text: 'content' });
  });
});
```

**Step 2: Run test to verify it fails**

```bash
cd scripts/x-browser && npx vitest run tests/api.test.ts
```

Expected: FAIL — `../src/api.js` does not exist.

**Step 3: Write implementation**

Write `scripts/x-browser/src/api.ts`:

```typescript
import { TwitterApi } from 'twitter-api-v2';

export interface XConfig {
  apiKey: string;
  apiSecret: string;
  accessToken: string;
  accessSecret: string;
}

export class ApiClient {
  private client: TwitterApi;
  private myId: string | null = null;

  constructor(config: XConfig) {
    this.client = new TwitterApi({
      appKey: config.apiKey,
      appSecret: config.apiSecret,
      accessToken: config.accessToken,
      accessSecret: config.accessSecret,
    });
  }

  async postTweet(text: string, mediaIds?: string[]): Promise<{ id: string; text: string }> {
    const options = mediaIds ? { media: { media_ids: mediaIds } } : undefined;
    const result = await this.client.v2.tweet(text, options);
    return { id: result.data.id, text: result.data.text };
  }

  async postThread(tweets: string[]): Promise<string[]> {
    const ids: string[] = [];
    let lastId: string | undefined;
    for (const text of tweets) {
      const options = lastId ? { reply: { in_reply_to_tweet_id: lastId } } : undefined;
      const result = await this.client.v2.tweet(text, options);
      ids.push(result.data.id);
      lastId = result.data.id;
    }
    return ids;
  }

  async uploadMedia(filePath: string): Promise<string> {
    return await this.client.v1.uploadMedia(filePath);
  }

  async follow(userId: string): Promise<void> {
    const myId = await this.getMyId();
    await this.client.v2.follow(myId, userId);
  }

  async unfollow(userId: string): Promise<void> {
    const myId = await this.getMyId();
    await this.client.v2.unfollow(myId, userId);
  }

  async likeTweet(tweetId: string): Promise<void> {
    const myId = await this.getMyId();
    await this.client.v2.like(myId, tweetId);
  }

  async retweet(tweetId: string): Promise<void> {
    const myId = await this.getMyId();
    await this.client.v2.retweet(myId, tweetId);
  }

  async fetchTweet(tweetId: string): Promise<{ id: string; text: string }> {
    const result = await this.client.v2.singleTweet(tweetId);
    return { id: result.data.id, text: result.data.text };
  }

  async deleteTweet(tweetId: string): Promise<void> {
    await this.client.v2.deleteTweet(tweetId);
  }

  private async getMyId(): Promise<string> {
    if (!this.myId) {
      const me = await this.client.v2.me();
      this.myId = me.data.id;
    }
    return this.myId;
  }
}
```

**Step 4: Run tests**

```bash
cd scripts/x-browser && npx vitest run tests/api.test.ts
```

Expected: 6 tests PASS.

**Step 5: Commit**

```bash
git add scripts/x-browser/src/api.ts scripts/x-browser/tests/api.test.ts
git commit -m "feat: API client wrapper around twitter-api-v2"
```

---

### Task 4: DOM selectors module

**Files:**
- Create: `scripts/x-browser/src/selectors.ts`

No tests needed — this is a constants file. Centralised so it's easy to update when X changes their DOM.

**Step 1: Write the selectors**

Write `scripts/x-browser/src/selectors.ts`:

```typescript
/**
 * Centralised DOM selectors for X.com.
 * Update these when X changes their UI structure.
 */
export const SELECTORS = {
  /** The reply text input area on a tweet page */
  replyTextarea: 'div[data-testid="tweetTextarea_0"]',
  /** The reply submit button */
  replyButton: 'button[data-testid="tweetButton"]',
  /** A tweet article in the timeline */
  tweetArticle: 'article[data-testid="tweet"]',
  /** The tweet text content */
  tweetText: 'div[data-testid="tweetText"]',
  /** Login redirect indicator — if this appears, cookies are expired */
  loginForm: 'form[action="/i/flow/login"]',
  /** The "What is happening?!" composer area (used to confirm page loaded) */
  replyComposer: 'div[data-testid="tweetTextarea_0"]',
} as const;
```

**Step 2: Commit**

```bash
git add scripts/x-browser/src/selectors.ts
git commit -m "feat: centralised X.com DOM selectors"
```

---

### Task 5: Browser session — reply via Playwright

**Files:**
- Create: `scripts/x-browser/src/browser.ts`
- Create: `scripts/x-browser/tests/browser.test.ts`

This is the core module. Uses Playwright Firefox with injected cookies to navigate to a tweet and post a reply through the DOM.

**Step 1: Write the failing test**

The browser tests mock Playwright since we can't run a real browser in unit tests. We test that the correct sequence of Playwright calls is made.

Write `scripts/x-browser/tests/browser.test.ts`:

```typescript
import { describe, it, expect, vi, beforeEach } from 'vitest';
import { BrowserSession } from '../src/browser.js';
import type { PlaywrightCookie } from '../src/cookies.js';

// Mock playwright
const mockPage = {
  goto: vi.fn(),
  waitForSelector: vi.fn(),
  click: vi.fn(),
  fill: vi.fn(),
  keyboard: { type: vi.fn() },
  waitForURL: vi.fn(),
  url: vi.fn(),
  screenshot: vi.fn(),
  locator: vi.fn(),
  waitForTimeout: vi.fn(),
  close: vi.fn(),
};

const mockContext = {
  addCookies: vi.fn(),
  newPage: vi.fn().mockResolvedValue(mockPage),
  close: vi.fn(),
};

const mockBrowser = {
  newContext: vi.fn().mockResolvedValue(mockContext),
  close: vi.fn(),
};

vi.mock('playwright', () => ({
  firefox: {
    launch: vi.fn().mockResolvedValue(mockBrowser),
  },
}));

vi.mock('../src/cookies.js', () => ({
  extractTwitterCookies: vi.fn().mockReturnValue([
    { name: 'auth_token', value: 'abc', domain: '.x.com', path: '/', expires: 99999, httpOnly: true, secure: true, sameSite: 'None' },
    { name: 'ct0', value: 'csrf', domain: '.x.com', path: '/', expires: 99999, httpOnly: false, secure: true, sameSite: 'Strict' },
  ] satisfies PlaywrightCookie[]),
  DEFAULT_COOKIE_DB: '/mock/cookies.sqlite',
}));

describe('BrowserSession', () => {
  let session: BrowserSession;

  beforeEach(() => {
    vi.clearAllMocks();
    session = new BrowserSession();
  });

  it('should inject cookies on launch', async () => {
    await session.launch();
    expect(mockContext.addCookies).toHaveBeenCalledWith(
      expect.arrayContaining([
        expect.objectContaining({ name: 'auth_token', value: 'abc' }),
      ])
    );
  });

  it('should navigate to tweet URL when replying', async () => {
    await session.launch();

    // Mock the reply flow DOM interactions
    const mockReplyBox = {
      waitFor: vi.fn(),
      click: vi.fn(),
      fill: vi.fn(),
    };
    const mockSubmitButton = {
      waitFor: vi.fn(),
      click: vi.fn(),
    };
    mockPage.locator
      .mockReturnValueOnce(mockReplyBox)   // reply textarea
      .mockReturnValueOnce(mockReplyBox)   // reply textarea (for fill)
      .mockReturnValueOnce(mockSubmitButton); // submit button

    mockPage.waitForSelector.mockResolvedValue(true);
    mockPage.url.mockReturnValue('https://x.com/someone/status/123');

    // Mock finding the new reply
    const mockTweets = {
      count: vi.fn().mockResolvedValue(2),
      last: vi.fn().mockReturnValue({
        getAttribute: vi.fn().mockResolvedValue(null),
        locator: vi.fn().mockReturnValue({
          textContent: vi.fn().mockResolvedValue('my reply text'),
        }),
      }),
    };
    mockPage.locator.mockReturnValueOnce(mockTweets);

    await session.reply('123', 'my reply text', 'someone');

    expect(mockPage.goto).toHaveBeenCalledWith(
      'https://x.com/someone/status/123',
      expect.any(Object)
    );
  });

  it('should close browser on close()', async () => {
    await session.launch();
    await session.close();
    expect(mockBrowser.close).toHaveBeenCalled();
  });
});
```

**Step 2: Run test to verify it fails**

```bash
cd scripts/x-browser && npx vitest run tests/browser.test.ts
```

Expected: FAIL — `../src/browser.js` does not exist.

**Step 3: Write implementation**

Write `scripts/x-browser/src/browser.ts`:

```typescript
import { firefox, type Browser, type BrowserContext, type Page } from 'playwright';
import { extractTwitterCookies, DEFAULT_COOKIE_DB } from './cookies.js';
import { SELECTORS } from './selectors.js';

export interface ReplyResult {
  success: boolean;
  tweetUrl?: string;
  error?: string;
  screenshotPath?: string;
}

export class BrowserSession {
  private browser: Browser | null = null;
  private context: BrowserContext | null = null;
  private page: Page | null = null;
  private cookieDbPath: string;

  constructor(cookieDbPath?: string) {
    this.cookieDbPath = cookieDbPath ?? DEFAULT_COOKIE_DB;
  }

  async launch(): Promise<void> {
    this.browser = await firefox.launch({ headless: false });
    this.context = await this.browser.newContext({
      viewport: { width: 1280, height: 900 },
      userAgent: 'Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:135.0) Gecko/20100101 Firefox/135.0',
    });

    const cookies = extractTwitterCookies(this.cookieDbPath);
    await this.context.addCookies(cookies);

    this.page = await this.context.newPage();
  }

  async reply(tweetId: string, text: string, authorHandle: string): Promise<ReplyResult> {
    if (!this.page) {
      await this.launch();
    }
    const page = this.page!;

    try {
      // Navigate to the tweet
      const url = `https://x.com/${authorHandle}/status/${tweetId}`;
      await page.goto(url, { waitUntil: 'domcontentloaded', timeout: 30000 });

      // Wait for tweet content to load
      await page.waitForSelector(SELECTORS.tweetArticle, { timeout: 15000 });

      // Small delay to let the page settle
      await page.waitForTimeout(2000);

      // Click the reply textarea
      const replyBox = page.locator(SELECTORS.replyTextarea);
      await replyBox.waitFor({ timeout: 10000 });
      await replyBox.click();

      // Type the reply
      const replyInput = page.locator(SELECTORS.replyTextarea);
      await replyInput.fill(text);

      // Click the reply button
      const submitBtn = page.locator(SELECTORS.replyButton);
      await submitBtn.waitFor({ timeout: 5000 });
      await submitBtn.click();

      // Wait for the reply to appear
      await page.waitForTimeout(3000);

      return { success: true, tweetUrl: page.url() };
    } catch (error) {
      const screenshotPath = `scripts/x-browser/error-${Date.now()}.png`;
      try {
        await page.screenshot({ path: screenshotPath });
      } catch {}
      return {
        success: false,
        error: error instanceof Error ? error.message : String(error),
        screenshotPath,
      };
    }
  }

  async close(): Promise<void> {
    if (this.browser) {
      await this.browser.close();
      this.browser = null;
      this.context = null;
      this.page = null;
    }
  }
}
```

**Step 4: Run tests**

```bash
cd scripts/x-browser && npx vitest run tests/browser.test.ts
```

Expected: 3 tests PASS.

**Step 5: Commit**

```bash
git add scripts/x-browser/src/browser.ts scripts/x-browser/src/selectors.ts scripts/x-browser/tests/browser.test.ts
git commit -m "feat: browser session with Playwright reply flow"
```

---

### Task 6: XClient — unified entry point

**Files:**
- Create: `scripts/x-browser/src/index.ts`
- Create: `scripts/x-browser/tests/client.test.ts`

**Step 1: Write the failing test**

Write `scripts/x-browser/tests/client.test.ts`:

```typescript
import { describe, it, expect, vi, beforeEach } from 'vitest';
import { XClient } from '../src/index.js';

// Mock the sub-modules
vi.mock('../src/api.js', () => {
  const mockApi = {
    postTweet: vi.fn().mockResolvedValue({ id: '1', text: 'hello' }),
    postThread: vi.fn().mockResolvedValue(['1', '2']),
    uploadMedia: vi.fn().mockResolvedValue('media_1'),
    follow: vi.fn(),
    unfollow: vi.fn(),
    likeTweet: vi.fn(),
    retweet: vi.fn(),
    fetchTweet: vi.fn().mockResolvedValue({ id: '99', text: 'fetched' }),
    deleteTweet: vi.fn(),
  };
  return {
    ApiClient: vi.fn().mockImplementation(() => mockApi),
    __mockApi: mockApi,
  };
});

vi.mock('../src/browser.js', () => {
  const mockBrowser = {
    launch: vi.fn(),
    reply: vi.fn().mockResolvedValue({ success: true, tweetUrl: 'https://x.com/...' }),
    close: vi.fn(),
  };
  return {
    BrowserSession: vi.fn().mockImplementation(() => mockBrowser),
    __mockBrowser: mockBrowser,
  };
});

describe('XClient', () => {
  let client: XClient;

  beforeEach(() => {
    vi.clearAllMocks();
    client = new XClient({
      apiKey: 'k', apiSecret: 's',
      accessToken: 't', accessSecret: 'a',
    });
  });

  it('should route postTweet to API', async () => {
    const { __mockApi } = await import('../src/api.js') as any;
    const result = await client.postTweet('hello');
    expect(__mockApi.postTweet).toHaveBeenCalledWith('hello', undefined);
    expect(result).toEqual({ id: '1', text: 'hello' });
  });

  it('should route postThread to API', async () => {
    const { __mockApi } = await import('../src/api.js') as any;
    const ids = await client.postThread(['a', 'b']);
    expect(__mockApi.postThread).toHaveBeenCalledWith(['a', 'b']);
    expect(ids).toEqual(['1', '2']);
  });

  it('should route reply to browser', async () => {
    const { __mockBrowser } = await import('../src/browser.js') as any;
    const result = await client.reply('123', 'my reply', 'someone');
    expect(__mockBrowser.reply).toHaveBeenCalledWith('123', 'my reply', 'someone');
    expect(result.success).toBe(true);
  });

  it('should route follow to API', async () => {
    const { __mockApi } = await import('../src/api.js') as any;
    await client.follow('user1');
    expect(__mockApi.follow).toHaveBeenCalledWith('user1');
  });

  it('should close browser on close()', async () => {
    const { __mockBrowser } = await import('../src/browser.js') as any;
    // Trigger browser init by calling reply
    await client.reply('1', 'test', 'user');
    await client.close();
    expect(__mockBrowser.close).toHaveBeenCalled();
  });
});
```

**Step 2: Run test to verify it fails**

```bash
cd scripts/x-browser && npx vitest run tests/client.test.ts
```

Expected: FAIL — `../src/index.js` does not exist.

**Step 3: Write implementation**

Write `scripts/x-browser/src/index.ts`:

```typescript
import { ApiClient, type XConfig } from './api.js';
import { BrowserSession, type ReplyResult } from './browser.js';

export type { XConfig } from './api.js';
export type { ReplyResult } from './browser.js';
export type { PlaywrightCookie } from './cookies.js';

export class XClient {
  private api: ApiClient;
  private browser: BrowserSession | null = null;
  private cookieDbPath?: string;

  constructor(config: XConfig, cookieDbPath?: string) {
    this.api = new ApiClient(config);
    this.cookieDbPath = cookieDbPath;
  }

  // --- Browser-routed (API blocked) ---

  async reply(tweetId: string, text: string, authorHandle: string): Promise<ReplyResult> {
    if (!this.browser) {
      this.browser = new BrowserSession(this.cookieDbPath);
    }
    return this.browser.reply(tweetId, text, authorHandle);
  }

  // --- API-routed ---

  async postTweet(text: string, mediaIds?: string[]) {
    return this.api.postTweet(text, mediaIds);
  }

  async postThread(tweets: string[]) {
    return this.api.postThread(tweets);
  }

  async uploadMedia(filePath: string) {
    return this.api.uploadMedia(filePath);
  }

  async follow(userId: string) {
    return this.api.follow(userId);
  }

  async unfollow(userId: string) {
    return this.api.unfollow(userId);
  }

  async likeTweet(tweetId: string) {
    return this.api.likeTweet(tweetId);
  }

  async retweet(tweetId: string) {
    return this.api.retweet(tweetId);
  }

  async fetchTweet(tweetId: string) {
    return this.api.fetchTweet(tweetId);
  }

  async deleteTweet(tweetId: string) {
    return this.api.deleteTweet(tweetId);
  }

  async close(): Promise<void> {
    if (this.browser) {
      await this.browser.close();
    }
  }
}
```

**Step 4: Run all tests**

```bash
cd scripts/x-browser && npx vitest run
```

Expected: All tests PASS across all 4 test files.

**Step 5: Commit**

```bash
git add scripts/x-browser/src/index.ts scripts/x-browser/tests/client.test.ts
git commit -m "feat: XClient unified entry point"
```

---

### Task 7: E2E smoke test — real browser reply

**Files:**
- Create: `scripts/x-browser/tests/e2e-reply.ts`

This is a manual smoke test (not in the vitest suite). It launches a real browser, uses real cookies, replies to one of your own tweets, then deletes the reply.

**Step 1: Write the smoke test script**

Write `scripts/x-browser/tests/e2e-reply.ts`:

```typescript
/**
 * E2E smoke test: reply to own tweet via browser, then delete via API.
 *
 * Usage: npx tsx tests/e2e-reply.ts <tweet_id> <author_handle>
 *
 * If no args, uses the latest @AIOMarketMaker tweet.
 */
import { XClient } from '../src/index.js';
import { readFileSync } from 'fs';

const config = JSON.parse(readFileSync('../../scripts/x-config.json', 'utf-8'));
const client = new XClient(config);

const tweetId = process.argv[2];
const handle = process.argv[3] || 'AIOMarketMaker';

if (!tweetId) {
  console.error('Usage: npx tsx tests/e2e-reply.ts <tweet_id> [author_handle]');
  process.exit(1);
}

async function main() {
  console.log(`Replying to tweet ${tweetId} by @${handle}...`);
  const result = await client.reply(tweetId, 'E2E smoke test reply — will delete shortly', handle);
  console.log('Result:', result);

  if (result.success) {
    console.log('Reply posted. Check browser to verify, then press Enter to clean up...');
    // Keep browser open for inspection
    await new Promise(resolve => process.stdin.once('data', resolve));
  }

  await client.close();
  console.log('Done.');
}

main().catch(e => { console.error(e); process.exit(1); });
```

**Step 2: Run against a real tweet (manual)**

```bash
cd scripts/x-browser && npx tsx tests/e2e-reply.ts <your-tweet-id> AIOMarketMaker
```

This will open a visible Firefox window, navigate to the tweet, and attempt to reply. Watch and verify visually.

**Step 3: Commit**

```bash
git add scripts/x-browser/tests/e2e-reply.ts
git commit -m "feat: E2E smoke test for browser reply"
```

---

### Task 8: Final wiring — add .gitignore and README

**Files:**
- Create: `scripts/x-browser/.gitignore`

**Step 1: Write .gitignore**

Write `scripts/x-browser/.gitignore`:

```
node_modules/
dist/
error-*.png
```

**Step 2: Commit**

```bash
git add scripts/x-browser/.gitignore
git commit -m "chore: x-browser gitignore"
```
