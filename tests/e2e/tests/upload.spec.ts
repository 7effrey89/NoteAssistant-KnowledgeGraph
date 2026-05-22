import { test, expect, Request } from '@playwright/test';
import * as path from 'path';

const SAMPLE_MARKDOWN = path.resolve(__dirname, '..', '..', '..', 'asset', 'summary.md');

test.describe('Markdown upload', () => {
  test('clicking Upload & Analyze posts the file and shows decomposition', async ({ page }) => {
    const consoleErrors: string[] = [];
    page.on('pageerror', (err) => consoleErrors.push(`pageerror: ${err.message}`));
    page.on('console', (msg) => {
      if (msg.type() === 'error') {
        const text = msg.text();
        if (text.includes('Failed to load resource: the server responded with a status of 400')) {
          return;
        }
        if (text.includes('Failed to load resource: the server responded with a status of 404')) {
          return;
        }
        consoleErrors.push(`console.error: ${text}`);
      }
    });

    await page.goto('/');
    await expect(page.locator('h1')).toContainText('Knowledge Graph Workbench');

    const uploadRequest = page.waitForRequest(
      (req: Request) => req.url().endsWith('/api/documents/upload') && req.method() === 'POST'
    );
    const uploadResponse = page.waitForResponse(
      (res) => res.url().endsWith('/api/documents/upload') && res.request().method() === 'POST'
    );

    await page.setInputFiles('#markdownFile', SAMPLE_MARKDOWN);
    await page.click('#uploadBtn');

    const req = await uploadRequest;
    expect(req.method()).toBe('POST');
    expect((req.headers()['content-type'] ?? '').toLowerCase()).toContain('multipart/form-data');

    const res = await uploadResponse;
    expect(res.ok(), `Upload returned ${res.status()}`).toBeTruthy();

    const body = await res.json();
    expect(body.documentId).toBeGreaterThan(0);
    expect(Array.isArray(body.chunks)).toBeTruthy();
    expect(body.chunks.length).toBeGreaterThan(0);

    await expect(page.locator('#uploadMessage')).toContainText(/Document \d+ analyzed/i, { timeout: 15_000 });

    await page.click('#tab-decomp-btn');
    const chunkItems = page.locator('#chunksOutput li');
    await expect(chunkItems.first()).toBeVisible();
    expect(await chunkItems.count()).toBeGreaterThan(0);

    expect(consoleErrors, consoleErrors.join('\n')).toEqual([]);
  });

  test('clicking Upload & Analyze without selecting a file shows validation message', async ({ page }) => {
    await page.goto('/');
    await page.click('#uploadBtn');
    await expect(page.locator('#uploadMessage')).toContainText(/select a \.md file/i);
  });
});
