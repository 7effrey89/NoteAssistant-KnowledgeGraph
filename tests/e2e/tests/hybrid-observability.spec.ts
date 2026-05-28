import { test, expect } from '@playwright/test';

test.describe('Hybrid observability', () => {
  test('renders chunk provenance trace and percentage breakdown', async ({ page }) => {
    await page.route('**/api/retrieval/hybrid', async (route) => {
      const body = {
        success: true,
        error: null,
        detectedEntities: ['microsoft', 'openai'],
        graphEntities: ['microsoft', 'openai', 'azure'],
        matchedEntities: ['microsoft', 'openai'],
        chunks: [
          {
            id: 1001,
            documentId: 42,
            chunkIndex: 3,
            content: 'Microsoft partnered with OpenAI and integrated models into Azure.',
            distance: 0.14,
            vectorRank: 1,
            keywordRank: 2,
            score: 0.83,
            source: 'entity',
          },
        ],
        promptContext: 'test prompt context',
        retrievalOrder: 'Entity match -> Graph expansion -> Hybrid retrieval -> LLM',
        answer: 'Stubbed answer for observability test.',
        trace: {
          question: 'How does Microsoft work with OpenAI?',
          steps: [
            {
              name: 'mode-router',
              summary: 'Resolved retrieval mode: path',
              detail: 'Mode rationale: Explicit mode path for test.\nTraversal hops: 3',
            },
            {
              name: 'graph-expansion',
              summary: 'Expanded to 3 graph entities (maxHops=3)',
              detail: [
                'Purpose: expand each seed entity by traversing graph neighbors up to the selected hop count.',
                '',
                'Parameterized SQL:',
                'SELECT name::text AS name',
                'FROM ag_catalog.cypher(@graph_name, $cypher$',
                '    MATCH (a)-[*1..@max_hops]-(b)',
                '    WHERE a.name = "@seed_name"',
                '    RETURN DISTINCT b.name AS name',
                '    LIMIT 50',
                '$cypher$) AS (name agtype);',
                '',
                'Executable SQL:',
                "-- seed: microsoft",
                "SELECT name::text AS name FROM ag_catalog.cypher('knowledge_graph', $cypher$ MATCH (a)-[*1..3]-(b) WHERE a.name = \"microsoft\" RETURN DISTINCT b.name AS name LIMIT 50 $cypher$) AS (name agtype);"
              ].join('\n'),
            },
            {
              name: 'chunk-provenance',
              summary: 'Chunk provenance breakdown',
              detail: '- entity: 3 (60.0%)\n- path-evidence: 2 (40.0%)',
            },
            {
              name: 'embedding',
              summary: 'Generated query embedding',
              detail: 'Output:\n[0.1,0.2,0.3]',
            },
            {
              name: 'vector-search',
              summary: 'Hybrid chunk retrieval returned 1 chunks',
              detail: 'trace disabled in e2e mock',
            },
          ],
        },
        clarificationQuestion: null,
        rewrittenQuestion: 'How does Microsoft work with OpenAI?',
        systemPrompt: 'test-answer-system',
        analysisSystemPrompt: 'test-analysis-system',
        graphRelationships: [],
        resolvedRetrievalMode: 'path',
        retrievalModeRationale: 'Explicit mode path for test',
        chunkSourceBreakdown: [
          { source: 'entity', count: 3, percentage: 60.0 },
          { source: 'path-evidence', count: 2, percentage: 40.0 },
        ],
        resolvedTraversalHops: 3,
      };

      await route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify(body),
      });
    });

    await page.goto('/');

    await page.fill('#hybridQuestion', 'How does Microsoft work with OpenAI?');
    await page.selectOption('#hybridModeSelect', 'path');
    await page.check('#hybridTraceToggle');
    await page.check('#hybridAnswerToggle');
    await page.click('#runHybridBtn');

    await expect(page.locator('#hybridStatus')).toContainText('Retrieval complete.', { timeout: 15000 });
    await expect(page.locator('#hybridProcessMap')).toContainText('Chunk source breakdown');
    await expect(page.locator('#hybridProcessMap')).toContainText('entity: 3 (60.0%)');
    await expect(page.locator('#hybridProcessMap')).toContainText('path-evidence: 2 (40.0%)');
    await expect(page.locator('#hybridProcessMap')).toContainText('Chunk provenance trace');
    await expect(page.locator('#hybridProcessMap')).toContainText('- entity: 3 (60.0%)');
    await expect(page.locator('#hybridProcessMap')).toContainText('Graph expansion query');
    await expect(page.locator('#hybridProcessMap')).toContainText('Parameterized');
    await expect(page.locator('#hybridProcessMap')).toContainText('Executable');

    const expansionCard = page.locator('.process-bubble').filter({ hasText: 'Graph expansion query' }).first();
    await expansionCard.getByRole('button', { name: 'Executable' }).click();
    await expect(expansionCard).toContainText("-- seed: microsoft");
  });
});
