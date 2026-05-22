import { test, expect, APIRequestContext } from '@playwright/test';
import * as path from 'path';

const SAMPLE_MARKDOWN = path.resolve(__dirname, '..', '..', '..', 'asset', 'summary.md');
const BACKEND_URL = process.env.BACKEND_BASE_URL ?? 'http://localhost:5070';
const AGE_HEALTH_TIMEOUT_MS = 120_000;

async function waitForAgeReady(request: APIRequestContext, timeoutMs: number) {
  const deadline = Date.now() + timeoutMs;
  let lastStatus = 0;
  let lastDetail = '';

  while (Date.now() < deadline) {
    const response = await request.get(`${BACKEND_URL}/api/health/age`, { timeout: 15_000 });
    lastStatus = response.status();
    if (response.ok()) {
      return;
    }

    const payload = await response.json().catch(() => null as unknown);
    lastDetail = (payload as { detail?: string } | null)?.detail ?? '';
    await new Promise((resolve) => setTimeout(resolve, 1500));
  }

  throw new Error(`AGE health not ready after ${timeoutMs}ms (status ${lastStatus}): ${lastDetail}`);
}

test.describe('Graph explorer', () => {
  test('assistant suggestion drives graph visual', async ({ page }) => {
    test.setTimeout(120_000);
    const health = await page.request.get(`${BACKEND_URL}/api/health/db`);
    if (!health.ok()) {
      test.skip(true, 'Database not ready for graph explorer.');
    }

    await page.goto('/');

    const uploadResponse = page.waitForResponse(
      (res) => res.url().endsWith('/api/documents/upload') && res.request().method() === 'POST'
    );

    await page.setInputFiles('#markdownFile', SAMPLE_MARKDOWN);
    await page.click('#uploadBtn');

    const upload = await uploadResponse;
    const uploadPayload = await upload.json();
    expect(upload.ok(), `Upload returned ${upload.status()}`).toBeTruthy();

    const ingestResponse = page.waitForResponse(
      (res) => res.url().includes(`/api/documents/${uploadPayload.documentId}/ingest`) && res.request().method() === 'POST'
    );

    await expect(page.locator('#ingestBtn')).toBeEnabled({ timeout: 30_000 });
    await page.click('#ingestBtn');
    const ingest = await ingestResponse;
    const ingestPayload = await ingest.json();
    expect(ingest.ok(), `Ingest returned ${ingest.status()}`).toBeTruthy();
    expect(ingestPayload?.status?.state).toBe('Completed');
    if (!ingestPayload?.sqlStatements?.length) {
      console.log('Ingest payload missing sqlStatements:', JSON.stringify(ingestPayload, null, 2));
    }
    if (ingestPayload?.graphName) {
      console.log(`Ingest graphName: ${ingestPayload.graphName}`);
    }

    const graphResponse = page.waitForResponse(
      (res) => res.url().includes(`/api/documents/${uploadPayload.documentId}/graph`) && res.request().method() === 'POST'
    );
    await page.click('#initGraphBtn');
    const graph = await graphResponse;
    expect(graph.ok(), `Graph build returned ${graph.status()}`).toBeTruthy();

    await waitForAgeReady(page.request, AGE_HEALTH_TIMEOUT_MS);

    const assistResponse = page.waitForResponse(
      (res) => res.url().endsWith('/api/query/assist') && res.request().method() === 'POST'
    );
    await page.fill('#assistantPrompt', 'show entity mentions');
    await page.click('#assistBtn');
    const assist = await assistResponse;
    expect(assist.ok(), `Assist returned ${assist.status()}`).toBeTruthy();
    await expect(page.locator('#queryInput')).toHaveValue(/RETURN c, r, e/i);

    const queryResponse = page.waitForResponse(
      (res) => res.url().endsWith('/api/query') && res.request().method() === 'POST'
    );
    await page.click('#runQueryBtn');
    const query = await queryResponse;
    const queryPayload = await query.json();
    expect(query.ok(), `Query returned ${query.status()}`).toBeTruthy();
    if (!queryPayload?.nodes?.length) {
      console.log('Query payload without nodes:', JSON.stringify(queryPayload, null, 2));
    }
    expect(queryPayload?.nodes?.length ?? 0).toBeGreaterThan(0);
    await expect(page.locator('#graphView canvas')).toBeVisible({ timeout: 15_000 });
  });
});
