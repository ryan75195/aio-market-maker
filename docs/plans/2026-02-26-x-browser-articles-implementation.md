# X Browser Articles CRUD Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Add createArticle, listArticles, updateArticle, and deleteArticle methods to the x-browser BrowserSession.

**Architecture:** Each operation follows the existing pattern — navigate to a URL, interact with the DOM via Playwright, return a typed result. Article selectors are unknown and must be discovered before implementation. All new selectors go in `selectors.ts`. Unit tests mock Playwright (same pattern as `timeline.test.ts`). An E2E discovery script runs first to map the DOM.

**Tech Stack:** TypeScript, Playwright (Firefox), vitest

---

### Task 0: Discover Article Editor DOM Selectors

This is a prerequisite for all other tasks. We need to map the article editor's DOM before writing any tests or implementation.

**Files:**
- Create: `scripts/x-browser/discover-article-selectors.ts`

**Step 1: Write a discovery script**

```typescript
import { BrowserSession } from './src/index.js';

const session = new BrowserSession();
await session.launch();
const page = (session as any).page!;

// 1. Find the article editor entry point
console.log('=== Navigating to article editor ===');
await page.goto('https://x.com/compose/article', { waitUntil: 'domcontentloaded', timeout: 30000 });
await page.waitForTimeout(5000);
console.log('URL:', page.url());

// Dump all data-testid attributes on the page
const testIds = await page.evaluate(() => {
  const els = document.querySelectorAll('[data-testid]');
  return Array.from(els).map(el => ({
    testId: el.getAttribute('data-testid'),
    tag: el.tagName.toLowerCase(),
    role: el.getAttribute('role'),
    text: el.textContent?.slice(0, 80),
  }));
});
console.log('\n=== data-testid elements ===');
for (const t of testIds) {
  console.log(`  [${t.tag}] data-testid="${t.testId}" role="${t.role}" text="${t.text}"`);
}

// Look for contenteditable elements (likely the title and body fields)
const editables = await page.evaluate(() => {
  const els = document.querySelectorAll('[contenteditable="true"]');
  return Array.from(els).map(el => ({
    tag: el.tagName.toLowerCase(),
    testId: el.getAttribute('data-testid'),
    placeholder: el.getAttribute('data-placeholder') || el.getAttribute('placeholder'),
    className: el.className?.slice(0, 100),
    text: el.textContent?.slice(0, 80),
  }));
});
console.log('\n=== contenteditable elements ===');
for (const e of editables) {
  console.log(`  [${e.tag}] testid="${e.testId}" placeholder="${e.placeholder}" class="${e.className}"`);
}

// Look for file inputs (cover image upload)
const fileInputs = await page.evaluate(() => {
  const els = document.querySelectorAll('input[type="file"]');
  return Array.from(els).map(el => ({
    accept: el.getAttribute('accept'),
    testId: el.getAttribute('data-testid'),
    name: el.getAttribute('name'),
  }));
});
console.log('\n=== file inputs ===');
for (const f of fileInputs) {
  console.log(`  accept="${f.accept}" testid="${f.testId}" name="${f.name}"`);
}

// Look for buttons (publish, save draft, etc)
const buttons = await page.evaluate(() => {
  const els = document.querySelectorAll('button, [role="button"]');
  return Array.from(els).map(el => ({
    tag: el.tagName.toLowerCase(),
    testId: el.getAttribute('data-testid'),
    text: el.textContent?.trim().slice(0, 50),
    ariaLabel: el.getAttribute('aria-label'),
  }));
});
console.log('\n=== buttons ===');
for (const b of buttons) {
  console.log(`  [${b.tag}] testid="${b.testId}" aria="${b.ariaLabel}" text="${b.text}"`);
}

// Take a screenshot for reference
await page.screenshot({ path: 'article-editor.png', fullPage: true });
console.log('\nScreenshot saved to article-editor.png');

// 2. Check the articles list page
console.log('\n=== Navigating to articles list ===');
await page.goto('https://x.com/ryan75195/articles', { waitUntil: 'domcontentloaded', timeout: 30000 });
await page.waitForTimeout(5000);
console.log('URL:', page.url());

const articleTestIds = await page.evaluate(() => {
  const els = document.querySelectorAll('[data-testid]');
  return Array.from(els).map(el => ({
    testId: el.getAttribute('data-testid'),
    tag: el.tagName.toLowerCase(),
    text: el.textContent?.slice(0, 80),
  }));
});
console.log('\n=== articles page data-testid elements ===');
for (const t of articleTestIds) {
  console.log(`  [${t.tag}] data-testid="${t.testId}" text="${t.text}"`);
}

// Look for article links
const articleLinks = await page.evaluate(() => {
  const els = document.querySelectorAll('a[href*="/article"]');
  return Array.from(els).map(el => ({
    href: el.getAttribute('href'),
    text: el.textContent?.slice(0, 80),
  }));
});
console.log('\n=== article links ===');
for (const a of articleLinks) {
  console.log(`  href="${a.href}" text="${a.text}"`);
}

await page.screenshot({ path: 'article-list.png', fullPage: true });
console.log('Screenshot saved to article-list.png');

await session.close();
```

**Step 2: Run the discovery script (headless: false for this one)**

Temporarily set `headless: false` in `browser.ts` so you can watch the browser navigate:

Run: `cd scripts/x-browser && npx tsx discover-article-selectors.ts`

**Step 3: Record the selectors**

From the output, identify and record:
- Article editor URL (might be `x.com/compose/article`, `x.com/i/articles/new`, or something else)
- Title field selector (likely a contenteditable div with a placeholder like "Title")
- Body field selector (likely a contenteditable div with a placeholder like "Tell your story...")
- Publish button selector (data-testid or text)
- Cover image upload selector (file input or button)
- Articles list URL pattern
- Individual article card selectors (for listArticles)
- Edit button selector (on an article page)
- Delete option selector (likely in a menu dropdown)

**Step 4: Update `selectors.ts` with discovered selectors**

Add a new section to `scripts/x-browser/src/selectors.ts`:

```typescript
export const SELECTORS = {
  // ... existing selectors ...

  /** Article editor - title field */
  articleTitle: '<discovered-selector>',
  /** Article editor - body field */
  articleBody: '<discovered-selector>',
  /** Article editor - publish button */
  articlePublish: '<discovered-selector>',
  /** Article editor - cover image upload input */
  articleCoverUpload: '<discovered-selector>',
  /** Article card on the articles list page */
  articleCard: '<discovered-selector>',
  /** Article edit button (on article view page) */
  articleEdit: '<discovered-selector>',
  /** Article delete option */
  articleDelete: '<discovered-selector>',
  /** Article delete confirmation */
  articleDeleteConfirm: '<discovered-selector>',
} as const;
```

**Step 5: Commit**

```bash
git add scripts/x-browser/src/selectors.ts scripts/x-browser/discover-article-selectors.ts
git commit -m "feat(x-browser): discover and add article editor DOM selectors"
```

---

### Task 1: Add Article Types and Exports

**Files:**
- Modify: `scripts/x-browser/src/browser.ts:1-22` (add interfaces)
- Modify: `scripts/x-browser/src/index.ts` (add exports)

**Step 1: Write the failing test**

Create `scripts/x-browser/tests/articles.test.ts`:

```typescript
import { describe, it, expect, vi, beforeEach } from 'vitest';
import { BrowserSession } from '../src/browser.js';
import type { ArticleSummary, ArticleResult, CreateArticleOptions, UpdateArticleOptions } from '../src/browser.js';

const { mockPage, mockContext, mockBrowser } = vi.hoisted(() => {
  const mockPage = {
    goto: vi.fn(),
    waitForSelector: vi.fn(),
    locator: vi.fn(),
    evaluate: vi.fn(),
    waitForTimeout: vi.fn(),
    url: vi.fn(),
    screenshot: vi.fn(),
    keyboard: { type: vi.fn() },
    click: vi.fn(),
    fill: vi.fn(),
    waitForURL: vi.fn(),
    close: vi.fn(),
    setInputFiles: vi.fn(),
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
  return { mockPage, mockContext, mockBrowser };
});

vi.mock('playwright', () => ({
  firefox: {
    launch: vi.fn().mockResolvedValue(mockBrowser),
  },
}));

vi.mock('../src/cookies.js', () => ({
  extractTwitterCookies: vi.fn().mockReturnValue([
    { name: 'auth_token', value: 'abc', domain: '.x.com', path: '/', expires: 99999, httpOnly: true, secure: true, sameSite: 'None' },
  ]),
  DEFAULT_COOKIE_DB: '/mock/cookies.sqlite',
}));

describe('BrowserSession.createArticle', () => {
  let session: BrowserSession;

  beforeEach(() => {
    vi.clearAllMocks();
    mockContext.newPage.mockResolvedValue(mockPage);
    mockBrowser.newContext.mockResolvedValue(mockContext);
    session = new BrowserSession();
  });

  it('should navigate to article editor', async () => {
    await session.launch();
    mockPage.waitForSelector.mockResolvedValue(true);

    const mockTitleField = { waitFor: vi.fn(), click: vi.fn() };
    const mockPublishBtn = { waitFor: vi.fn(), click: vi.fn() };
    mockPage.locator
      .mockReturnValueOnce(mockTitleField)   // title field
      .mockReturnValueOnce(mockPublishBtn);  // publish button

    mockPage.url.mockReturnValue('https://x.com/ryan75195/article/123');

    await session.createArticle({ title: 'Test Article', body: 'Hello world' });

    expect(mockPage.goto).toHaveBeenCalledWith(
      expect.stringContaining('article'),
      expect.any(Object),
    );
  });

  it('should type title and body', async () => {
    await session.launch();
    mockPage.waitForSelector.mockResolvedValue(true);

    const mockTitleField = { waitFor: vi.fn(), click: vi.fn() };
    const mockPublishBtn = { waitFor: vi.fn(), click: vi.fn() };
    mockPage.locator
      .mockReturnValueOnce(mockTitleField)
      .mockReturnValueOnce(mockPublishBtn);

    mockPage.url.mockReturnValue('https://x.com/ryan75195/article/123');

    await session.createArticle({ title: 'My Title', body: 'My body text' });

    const typeCalls = mockPage.keyboard.type.mock.calls;
    expect(typeCalls.length).toBeGreaterThanOrEqual(2);
    expect(typeCalls[0][0]).toBe('My Title');
    expect(typeCalls[1][0]).toBe('My body text');
  });

  it('should return success with article URL', async () => {
    await session.launch();
    mockPage.waitForSelector.mockResolvedValue(true);

    const mockTitleField = { waitFor: vi.fn(), click: vi.fn() };
    const mockPublishBtn = { waitFor: vi.fn(), click: vi.fn() };
    mockPage.locator
      .mockReturnValueOnce(mockTitleField)
      .mockReturnValueOnce(mockPublishBtn);

    mockPage.url.mockReturnValue('https://x.com/ryan75195/article/123');

    const result = await session.createArticle({ title: 'Test', body: 'Body' });

    expect(result.success).toBe(true);
    expect(result.articleUrl).toBe('https://x.com/ryan75195/article/123');
  });

  it('should return error on failure', async () => {
    await session.launch();
    mockPage.waitForSelector.mockRejectedValue(new Error('Timeout'));

    const result = await session.createArticle({ title: 'Test', body: 'Body' });

    expect(result.success).toBe(false);
    expect(result.error).toContain('Timeout');
  });
});
```

**Step 2: Run test to verify it fails**

Run: `cd scripts/x-browser && npx vitest run tests/articles.test.ts`
Expected: FAIL — `createArticle` does not exist, types not exported

**Step 3: Add types to browser.ts**

Add after the existing `ReplyResult` interface in `scripts/x-browser/src/browser.ts`:

```typescript
export interface ArticleSummary {
  id: string;
  title: string;
  url: string;
  date: Date;
}

export interface ArticleResult {
  success: boolean;
  articleUrl?: string;
  error?: string;
  screenshotPath?: string;
}

export interface CreateArticleOptions {
  title: string;
  body: string;
  coverImagePath?: string;
}

export interface UpdateArticleOptions {
  title?: string;
  body?: string;
}
```

**Step 4: Add stub createArticle method**

Add to the `BrowserSession` class:

```typescript
async createArticle(options: CreateArticleOptions): Promise<ArticleResult> {
  if (!this.page) {
    await this.launch();
  }
  const page = this.page!;

  try {
    // Navigate to article editor
    await page.goto('https://x.com/compose/article', { waitUntil: 'domcontentloaded', timeout: 30000 });
    await page.waitForSelector(SELECTORS.articleTitle, { timeout: 15000 });
    await page.waitForTimeout(2000);

    // Upload cover image if provided
    if (options.coverImagePath) {
      const fileInput = page.locator(SELECTORS.articleCoverUpload);
      await fileInput.setInputFiles(options.coverImagePath);
      await page.waitForTimeout(2000);
    }

    // Type title
    const titleField = page.locator(SELECTORS.articleTitle);
    await titleField.waitFor({ timeout: 10000 });
    await titleField.click({ force: true });
    await page.keyboard.type(options.title, { delay: 30 });

    // Tab or click to body field, then type body
    await page.keyboard.press('Tab');
    await page.waitForTimeout(500);
    await page.keyboard.type(options.body, { delay: 30 });

    // Click publish
    const publishBtn = page.locator(SELECTORS.articlePublish);
    await publishBtn.waitFor({ timeout: 5000 });
    await publishBtn.click({ force: true });

    await page.waitForTimeout(3000);

    return { success: true, articleUrl: page.url() };
  } catch (error) {
    const screenshotPath = `error-article-${Date.now()}.png`;
    try {
      await page.screenshot({ path: screenshotPath });
    } catch {
      // Screenshot may fail
    }
    return {
      success: false,
      error: error instanceof Error ? error.message : String(error),
      screenshotPath,
    };
  }
}
```

**Step 5: Update exports in index.ts**

Update `scripts/x-browser/src/index.ts`:

```typescript
export {
  BrowserSession,
  type ReplyResult,
  type TimelineTweet,
  type TimelineOptions,
  type ArticleSummary,
  type ArticleResult,
  type CreateArticleOptions,
  type UpdateArticleOptions,
} from './browser.js';
export { extractTwitterCookies, DEFAULT_COOKIE_DB, type PlaywrightCookie } from './cookies.js';
export { SELECTORS } from './selectors.js';
```

**Step 6: Run test to verify it passes**

Run: `cd scripts/x-browser && npx vitest run tests/articles.test.ts`
Expected: PASS (4 tests)

**Step 7: Run all tests to confirm no regressions**

Run: `cd scripts/x-browser && npx vitest run`
Expected: All existing tests still pass

**Step 8: Commit**

```bash
git add scripts/x-browser/src/browser.ts scripts/x-browser/src/index.ts scripts/x-browser/tests/articles.test.ts
git commit -m "feat(x-browser): add createArticle with types and tests"
```

---

### Task 2: Add listArticles Method

**Files:**
- Modify: `scripts/x-browser/src/browser.ts`
- Modify: `scripts/x-browser/tests/articles.test.ts`

**Step 1: Add failing tests to articles.test.ts**

Append a new describe block:

```typescript
describe('BrowserSession.listArticles', () => {
  let session: BrowserSession;

  beforeEach(() => {
    vi.clearAllMocks();
    mockContext.newPage.mockResolvedValue(mockPage);
    mockBrowser.newContext.mockResolvedValue(mockContext);
    session = new BrowserSession();
  });

  it('should navigate to user articles page', async () => {
    await session.launch();
    mockPage.waitForSelector.mockResolvedValue(true);

    const mockCards = { count: vi.fn().mockResolvedValue(0) };
    mockPage.locator.mockReturnValue(mockCards);
    mockPage.evaluate.mockResolvedValue(0);

    await session.listArticles('ryan75195');

    expect(mockPage.goto).toHaveBeenCalledWith(
      expect.stringContaining('ryan75195'),
      expect.any(Object),
    );
  });

  it('should return article summaries', async () => {
    await session.launch();
    mockPage.waitForSelector.mockResolvedValue(true);

    const mockCard = {
      locator: vi.fn().mockImplementation((selector: string) => {
        if (selector.includes('a[href')) {
          return { first: vi.fn().mockReturnValue({ getAttribute: vi.fn().mockResolvedValue('/ryan75195/article/456') }) };
        }
        if (selector === 'time') {
          return { first: vi.fn().mockReturnValue({ getAttribute: vi.fn().mockResolvedValue('2026-02-25T12:00:00.000Z') }) };
        }
        return { textContent: vi.fn().mockResolvedValue('My Article Title') };
      }),
    };

    const mockCards = {
      count: vi.fn().mockResolvedValue(1),
      nth: vi.fn().mockReturnValue(mockCard),
    };
    mockPage.locator.mockReturnValue(mockCards);
    mockPage.evaluate.mockResolvedValue(0);

    const results = await session.listArticles('ryan75195');

    expect(results).toHaveLength(1);
    expect(results[0]).toMatchObject({
      id: '456',
      title: 'My Article Title',
      url: expect.stringContaining('/article/456'),
    });
  });
});
```

**Step 2: Run test to verify it fails**

Run: `cd scripts/x-browser && npx vitest run tests/articles.test.ts`
Expected: FAIL — `listArticles` does not exist

**Step 3: Implement listArticles**

Add to `BrowserSession` in `browser.ts`:

```typescript
async listArticles(handle: string): Promise<ArticleSummary[]> {
  if (!this.page) {
    await this.launch();
  }
  const page = this.page!;

  await page.goto(`https://x.com/${handle}/articles`, { waitUntil: 'domcontentloaded', timeout: 30000 });
  await page.waitForSelector(SELECTORS.articleCard, { timeout: 15000 });
  await page.waitForTimeout(2000);

  const collected: ArticleSummary[] = [];
  const seenIds = new Set<string>();

  const cards = page.locator(SELECTORS.articleCard);
  const count = await cards.count();

  for (let i = 0; i < count; i++) {
    const card = cards.nth(i);

    const href = await card.locator('a[href*="/article/"]').first().getAttribute('href').catch(() => null);
    if (!href) { continue; }
    const idMatch = href.match(/\/article\/(\d+)/);
    if (!idMatch) { continue; }
    const id = idMatch[1];
    if (seenIds.has(id)) { continue; }
    seenIds.add(id);

    const title = await card.locator(SELECTORS.articleTitle).textContent().catch(() => '') ?? '';
    const datetime = await card.locator('time').first().getAttribute('datetime').catch(() => null);
    const date = datetime ? new Date(datetime) : new Date();

    collected.push({
      id,
      title,
      url: `https://x.com${href}`,
      date,
    });
  }

  return collected;
}
```

**Note:** The selectors used here (`articleCard`, `articleTitle` in list context) may need adjustment after Task 0 discovery. The title selector in the list view may differ from the editor's title field. Add a separate `articleCardTitle` selector if needed.

**Step 4: Run test to verify it passes**

Run: `cd scripts/x-browser && npx vitest run tests/articles.test.ts`
Expected: PASS

**Step 5: Commit**

```bash
git add scripts/x-browser/src/browser.ts scripts/x-browser/tests/articles.test.ts
git commit -m "feat(x-browser): add listArticles method with tests"
```

---

### Task 3: Add updateArticle Method

**Files:**
- Modify: `scripts/x-browser/src/browser.ts`
- Modify: `scripts/x-browser/tests/articles.test.ts`

**Step 1: Add failing tests**

Append to `articles.test.ts`:

```typescript
describe('BrowserSession.updateArticle', () => {
  let session: BrowserSession;

  beforeEach(() => {
    vi.clearAllMocks();
    mockContext.newPage.mockResolvedValue(mockPage);
    mockBrowser.newContext.mockResolvedValue(mockContext);
    session = new BrowserSession();
  });

  it('should navigate to article URL', async () => {
    await session.launch();
    mockPage.waitForSelector.mockResolvedValue(true);

    const mockEditBtn = { waitFor: vi.fn(), click: vi.fn() };
    const mockSaveBtn = { waitFor: vi.fn(), click: vi.fn() };
    mockPage.locator
      .mockReturnValueOnce(mockEditBtn)
      .mockReturnValueOnce(mockSaveBtn);

    mockPage.url.mockReturnValue('https://x.com/ryan75195/article/123');

    await session.updateArticle('https://x.com/ryan75195/article/123', { title: 'New Title' });

    expect(mockPage.goto).toHaveBeenCalledWith(
      'https://x.com/ryan75195/article/123',
      expect.any(Object),
    );
  });

  it('should return success on update', async () => {
    await session.launch();
    mockPage.waitForSelector.mockResolvedValue(true);

    const mockEditBtn = { waitFor: vi.fn(), click: vi.fn() };
    const mockSaveBtn = { waitFor: vi.fn(), click: vi.fn() };
    mockPage.locator
      .mockReturnValueOnce(mockEditBtn)
      .mockReturnValueOnce(mockSaveBtn);

    mockPage.url.mockReturnValue('https://x.com/ryan75195/article/123');

    const result = await session.updateArticle('https://x.com/ryan75195/article/123', { body: 'Updated body' });

    expect(result.success).toBe(true);
  });
});
```

**Step 2: Run test to verify it fails**

Run: `cd scripts/x-browser && npx vitest run tests/articles.test.ts`
Expected: FAIL — `updateArticle` does not exist

**Step 3: Implement updateArticle**

```typescript
async updateArticle(articleUrl: string, options: UpdateArticleOptions): Promise<ArticleResult> {
  if (!this.page) {
    await this.launch();
  }
  const page = this.page!;

  try {
    await page.goto(articleUrl, { waitUntil: 'domcontentloaded', timeout: 30000 });
    await page.waitForTimeout(2000);

    // Click edit button
    const editBtn = page.locator(SELECTORS.articleEdit);
    await editBtn.waitFor({ timeout: 10000 });
    await editBtn.click({ force: true });
    await page.waitForTimeout(2000);

    // Update title if provided
    if (options.title) {
      const titleField = page.locator(SELECTORS.articleTitle);
      await titleField.waitFor({ timeout: 10000 });
      await titleField.click({ force: true });
      await page.keyboard.press('Control+A');
      await page.keyboard.type(options.title, { delay: 30 });
    }

    // Update body if provided
    if (options.body) {
      const bodyField = page.locator(SELECTORS.articleBody);
      await bodyField.waitFor({ timeout: 10000 });
      await bodyField.click({ force: true });
      await page.keyboard.press('Control+A');
      await page.keyboard.type(options.body, { delay: 30 });
    }

    // Save/publish changes
    const saveBtn = page.locator(SELECTORS.articlePublish);
    await saveBtn.waitFor({ timeout: 5000 });
    await saveBtn.click({ force: true });
    await page.waitForTimeout(3000);

    return { success: true, articleUrl: page.url() };
  } catch (error) {
    const screenshotPath = `error-article-update-${Date.now()}.png`;
    try {
      await page.screenshot({ path: screenshotPath });
    } catch { }
    return {
      success: false,
      error: error instanceof Error ? error.message : String(error),
      screenshotPath,
    };
  }
}
```

**Step 4: Run test to verify it passes**

Run: `cd scripts/x-browser && npx vitest run tests/articles.test.ts`
Expected: PASS

**Step 5: Commit**

```bash
git add scripts/x-browser/src/browser.ts scripts/x-browser/tests/articles.test.ts
git commit -m "feat(x-browser): add updateArticle method with tests"
```

---

### Task 4: Add deleteArticle Method

**Files:**
- Modify: `scripts/x-browser/src/browser.ts`
- Modify: `scripts/x-browser/tests/articles.test.ts`

**Step 1: Add failing tests**

```typescript
describe('BrowserSession.deleteArticle', () => {
  let session: BrowserSession;

  beforeEach(() => {
    vi.clearAllMocks();
    mockContext.newPage.mockResolvedValue(mockPage);
    mockBrowser.newContext.mockResolvedValue(mockContext);
    session = new BrowserSession();
  });

  it('should navigate to article and delete it', async () => {
    await session.launch();
    mockPage.waitForSelector.mockResolvedValue(true);

    const mockDeleteBtn = { waitFor: vi.fn(), click: vi.fn() };
    const mockConfirmBtn = { waitFor: vi.fn(), click: vi.fn() };
    mockPage.locator
      .mockReturnValueOnce(mockDeleteBtn)
      .mockReturnValueOnce(mockConfirmBtn);

    const result = await session.deleteArticle('https://x.com/ryan75195/article/123');

    expect(mockPage.goto).toHaveBeenCalledWith(
      'https://x.com/ryan75195/article/123',
      expect.any(Object),
    );
    expect(result.success).toBe(true);
  });

  it('should return error on failure', async () => {
    await session.launch();
    mockPage.waitForSelector.mockRejectedValue(new Error('Not found'));

    const result = await session.deleteArticle('https://x.com/ryan75195/article/999');

    expect(result.success).toBe(false);
    expect(result.error).toContain('Not found');
  });
});
```

**Step 2: Run test to verify it fails**

Run: `cd scripts/x-browser && npx vitest run tests/articles.test.ts`
Expected: FAIL — `deleteArticle` does not exist

**Step 3: Implement deleteArticle**

```typescript
async deleteArticle(articleUrl: string): Promise<ArticleResult> {
  if (!this.page) {
    await this.launch();
  }
  const page = this.page!;

  try {
    await page.goto(articleUrl, { waitUntil: 'domcontentloaded', timeout: 30000 });
    await page.waitForTimeout(2000);

    // Click delete option
    const deleteBtn = page.locator(SELECTORS.articleDelete);
    await deleteBtn.waitFor({ timeout: 10000 });
    await deleteBtn.click({ force: true });
    await page.waitForTimeout(1000);

    // Confirm deletion
    const confirmBtn = page.locator(SELECTORS.articleDeleteConfirm);
    await confirmBtn.waitFor({ timeout: 5000 });
    await confirmBtn.click({ force: true });
    await page.waitForTimeout(3000);

    return { success: true };
  } catch (error) {
    const screenshotPath = `error-article-delete-${Date.now()}.png`;
    try {
      await page.screenshot({ path: screenshotPath });
    } catch { }
    return {
      success: false,
      error: error instanceof Error ? error.message : String(error),
      screenshotPath,
    };
  }
}
```

**Step 4: Run test to verify it passes**

Run: `cd scripts/x-browser && npx vitest run tests/articles.test.ts`
Expected: PASS

**Step 5: Run all tests**

Run: `cd scripts/x-browser && npx vitest run`
Expected: All tests pass (cookies, browser, timeline, articles)

**Step 6: Commit**

```bash
git add scripts/x-browser/src/browser.ts scripts/x-browser/tests/articles.test.ts
git commit -m "feat(x-browser): add deleteArticle method with tests"
```

---

### Task 5: Add CLI Scripts and E2E Smoke Test

**Files:**
- Create: `scripts/x-browser/create-article.ts`
- Create: `scripts/x-browser/list-articles.ts`

**Step 1: Create CLI scripts**

`scripts/x-browser/create-article.ts`:
```typescript
import { BrowserSession } from './src/index.js';
import { readFileSync } from 'fs';

const title = process.argv[2];
const bodyOrPath = process.argv[3];
const coverImagePath = process.argv[4];

if (!title || !bodyOrPath) {
  console.error('Usage: npx tsx create-article.ts <title> <body-or-file-path> [cover-image-path]');
  console.error('If body-or-file-path ends in .md or .txt, reads from file');
  process.exit(1);
}

const body = bodyOrPath.endsWith('.md') || bodyOrPath.endsWith('.txt')
  ? readFileSync(bodyOrPath, 'utf-8')
  : bodyOrPath;

const session = new BrowserSession();
const result = await session.createArticle({ title, body, coverImagePath });

console.log('Result:', JSON.stringify(result, null, 2));
await session.close();
```

`scripts/x-browser/list-articles.ts`:
```typescript
import { BrowserSession } from './src/index.js';

const handle = process.argv[2] || 'ryan75195';

const session = new BrowserSession();
const articles = await session.listArticles(handle);

console.log(`Found ${articles.length} articles from @${handle}:\n`);
for (const a of articles) {
  console.log(`[${a.date.toISOString().slice(0, 10)}] ${a.id}`);
  console.log(`  ${a.title}`);
  console.log(`  ${a.url}`);
  console.log();
}

await session.close();
```

**Step 2: Commit**

```bash
git add scripts/x-browser/create-article.ts scripts/x-browser/list-articles.ts
git commit -m "feat(x-browser): add article CLI scripts"
```

**Step 3: E2E smoke test (manual)**

Run with `headless: false` temporarily to watch:
```bash
cd scripts/x-browser && npx tsx create-article.ts "Test Article" "This is a test article body.\n\n## Section\n\nSome content here."
```

Verify it creates the article, then delete it manually or via:
```bash
cd scripts/x-browser && npx tsx -e "
import { BrowserSession } from './src/index.js';
const session = new BrowserSession();
const result = await session.deleteArticle('<article-url-from-above>');
console.log(result);
await session.close();
"
```

---

### Task 6: Update Social Skill Documentation

**Files:**
- Modify: `~/.claude/skills/social/skill.md`

Add an "Articles" section after the "Scraping Timelines via Browser" section documenting:
- How to create articles via CLI and programmatically
- How to list, update, and delete articles
- That body text supports markdown (typed, not pasted)
- Cover image is optional

**Commit:**

```bash
git add ~/.claude/skills/social/skill.md
git commit -m "docs: add articles feature to social skill"
```

---

### Task Order and Dependencies

```
Task 0 (discover selectors) → Task 1 (types + createArticle) → Task 2 (listArticles) → Task 3 (updateArticle) → Task 4 (deleteArticle) → Task 5 (CLI scripts + E2E) → Task 6 (docs)
```

Task 0 must complete first — all other tasks depend on the discovered selectors. Tasks 1-4 are sequential (each adds to the same files). Task 5 depends on all CRUD methods. Task 6 is independent but should go last.
