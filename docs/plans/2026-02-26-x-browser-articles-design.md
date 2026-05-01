# X Browser Articles Feature Design

**Goal:** Add full CRUD for X/Twitter Articles (long-form posts) to the x-browser Playwright client.

**Context:** Ryan has X Premium which unlocks Articles. The x-browser client already handles replies and timeline scraping via browser automation. Articles extend this with create, read, update, and delete operations.

## API Surface

New methods on `BrowserSession`:

| Method | Input | Output |
|--------|-------|--------|
| `createArticle(options)` | title, body, coverImagePath? | `ArticleResult` |
| `listArticles(handle)` | profile handle | `ArticleSummary[]` |
| `updateArticle(articleUrl, options)` | URL, new title?, new body? | `ArticleResult` |
| `deleteArticle(articleUrl)` | URL | `ArticleResult` |

## Types

```typescript
interface ArticleSummary {
  id: string;
  title: string;
  url: string;
  date: Date;
}

interface ArticleResult {
  success: boolean;
  articleUrl?: string;
  error?: string;
}

interface CreateArticleOptions {
  title: string;
  body: string;
  coverImagePath?: string;
}

interface UpdateArticleOptions {
  title?: string;
  body?: string;
}
```

## How Each Operation Works

### Create
1. Navigate to article editor (discover entry point URL)
2. Upload cover image if provided (file input or drag-drop)
3. Type title via `keyboard.type()`
4. Type body via `keyboard.type()` (markdown renders as typed in the editor)
5. Click publish button
6. Return article URL

### Read (List)
1. Navigate to `x.com/{handle}/articles` (or equivalent tab)
2. Scrape article cards (similar to `getTimeline` scroll approach)
3. Extract id, title, url, date from each

### Update
1. Navigate to article URL
2. Click edit button
3. Clear and retype title if provided
4. Clear and retype body if provided
5. Save changes

### Delete
1. Navigate to article URL
2. Find delete option (likely in a menu)
3. Confirm deletion

## Key Unknowns (Discover During Implementation)

- Article editor entry point URL (`x.com/i/articles/new`? or via compose menu?)
- DOM selectors for title field, body field, publish button, edit button, delete option
- Cover image upload mechanism (file input vs drag-drop)
- Articles tab URL pattern on profiles
- How the editor handles markdown typing (which syntax triggers formatting)

## Input Format

Body text is typed via `keyboard.type()`, not pasted. The X article editor recognises markdown syntax as you type (similar to Notion), so markdown input renders as formatted content. Plain text also works.

## Testing

- Unit tests with mocked Playwright (same pattern as reply and timeline tests)
- E2E smoke test script for manual verification
- Selectors centralised in `selectors.ts`
