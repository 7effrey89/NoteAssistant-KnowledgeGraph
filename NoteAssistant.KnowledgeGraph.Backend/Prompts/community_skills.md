# Community Tuning Skills -- Leiden CPM GraphRAG Communities

Use this guide when assessing whether graph community detection should be tuned for GraphRAG global search, temporal search, and community summarization.

The goal is not to maximize a graph metric in isolation. The goal is to produce communities that are coherent enough for an LLM to summarize and useful enough for retrieval.

---

# Current Community Model

The application uses Leiden CPM community detection over an undirected weighted entity graph.

Default assumptions:

- Algorithm: `LeidenCpm`
- Directed: `false`
- Seed: `42`
- CPM resolution: `0.25`
- Typed relationship weight: `2.0`
- Co-mention fallback weight: `1.0`
- Max communities summarized: `50`
- Communities with one entity are not summarized.

Edges are aggregated by unordered entity pair before Leiden runs. Repeated evidence should increase edge weight.

---

# Tuning Objective

A good community configuration produces:

- Clear themes that can be summarized in one concise LLM response.
- Community titles that look like meaningful topics, projects, products, customers, or workstreams.
- Few giant generic communities.
- Few tiny low-value pair communities.
- Stable global search results across repeated builds with the same seed.
- Useful global/temporal GraphRAG answers, not merely good clustering statistics.

The best configuration is the smallest resolution that avoids broad generic communities while preserving enough context for useful summaries.

---

# Required Inputs For Assessment

An agent should collect these inputs before recommending tuning changes:

## Detection Metrics

- algorithm
- resolution
- seed
- typedRelationshipWeight
- coMentionWeight
- total entity count
- total relationship count
- total communities
- singleton communities
- multi-entity communities
- largest community entity count
- p95 community entity count
- median community entity count
- smallest multi-entity community count
- number of communities selected for summarization
- number of communities over 50 entities
- number of communities with 2 or 3 entities

## Community Samples

Inspect at least:

- top 10 largest communities
- 5 medium communities near median size
- 5 small communities with 2-3 entities
- any community with obviously mixed topics
- any community with suspicious hub entities

For each sampled community, inspect:

- title
- entity count
- relationship count
- top entities
- relationship types
- generated summary
- source document spread

## Retrieval Quality Signals

Run or inspect a small query set:

- broad summary query
- customer-specific query
- product/platform-specific query
- temporal query
- trend/pattern query

For each query, assess:

- Were the selected communities relevant?
- Did the answer cite/use coherent themes?
- Did unrelated themes dilute the answer?
- Did the answer miss important communities because they were too fragmented?

---

# Core Tuning Sliders

## `cpmResolution`

Primary community size control.

Higher values create more/smaller communities.
Lower values create fewer/larger communities.

Starting range:

- `0.05`: broad communities
- `0.10`: medium-broad communities
- `0.15`: medium communities
- `0.25`: medium-fine communities
- `0.35`: fine communities
- `0.50`: very fine communities

Adjustment rules:

- If largest communities are too broad or generic, increase resolution.
- If most communities have 2-3 entities, decrease resolution.
- If global search misses themes because topics are scattered, decrease resolution.
- If summaries mention too many unrelated topics, increase resolution.

Change resolution gradually:

- Small change: multiply/divide by `1.25`
- Medium change: multiply/divide by `1.5`
- Large change: multiply/divide by `2.0`

Prefer one resolution change at a time.

## `typedRelationshipWeight`

Controls how strongly extracted typed relationships bind entities.

Default: `2.0`

Increase when:

- typed relationships should dominate weak co-mentions
- co-mention noise creates broad mixed communities
- entity pairs with explicit relationships are being separated too often

Decrease when:

- typed extraction is noisy
- incorrect relationships over-bind unrelated entities
- summaries reflect extraction artifacts instead of real themes

Suggested range: `1.0` to `4.0`

## `coMentionWeight`

Controls fallback edges from entities appearing in the same chunk.

Default: `1.0`

Increase when:

- typed relationships are sparse
- communities are too fragmented
- co-occurrence is a strong signal in the source documents

Decrease when:

- hub entities connect unrelated topics
- common entities make large mixed communities
- titles/summaries look like document-level bags of entities

Suggested range: `0.25` to `2.0`

## `maxCommunitiesToSummarize`

Controls cost and coverage after detection.

Default: `50`

Increase when:

- many coherent communities are omitted
- global search misses important long-tail themes

Decrease when:

- community build cost is too high
- many selected communities are low-value tiny topics

This does not change community detection. It only changes what gets summarized.

## `minCommunitySizeToSummarize`

Controls whether small communities should receive summaries.

Default: `2`

Increase when:

- many summaries say only that two entities are related
- global search is cluttered by low-value pair communities

Decrease only if two-entity relationships are important for retrieval.

---

# Diagnosis Patterns

## Communities Are Too Broad

Symptoms:

- largest community has more than 80-100 entities
- summaries list many unrelated topics
- titles contain many unrelated entities
- broad queries retrieve generic communities

Likely changes:

```json
{
  "cpmResolution": "increase",
  "coMentionWeight": "decrease if broadness comes from weak co-mentions",
  "typedRelationshipWeight": "increase if typed evidence should dominate"
}
```

## Communities Are Too Fragmented

Symptoms:

- most communities have 2-3 entities
- many summaries say only that entity A relates to entity B
- global search misses broader themes
- important product/customer themes split across many communities

Likely changes:

```json
{
  "cpmResolution": "decrease",
  "coMentionWeight": "increase if typed relationships are sparse",
  "minCommunitySizeToSummarize": "increase only if tiny summaries are noisy"
}
```

## Hub Entities Pollute Communities

Symptoms:

- entities like Microsoft, Azure, AI, data, customer, platform appear everywhere
- unrelated topics join around common terms
- community titles are dominated by generic entities

Likely changes:

```json
{
  "coMentionWeight": "decrease",
  "typedRelationshipWeight": "increase",
  "entityFiltering": "consider down-weighting or excluding generic hub entities"
}
```

## Typed Relationships Are Too Noisy

Symptoms:

- explicit relationships bind entities that are not truly related
- summaries repeat incorrect extraction claims
- communities look worse than co-mention groups

Likely changes:

```json
{
  "typedRelationshipWeight": "decrease",
  "relationshipExtraction": "inspect extraction prompt and confidence thresholds"
}
```

## Build Cost Is Too High

Symptoms:

- too many communities are summarized
- many summaries are tiny or low value
- community build takes too long

Likely changes:

```json
{
  "maxCommunitiesToSummarize": "decrease",
  "minCommunitySizeToSummarize": "increase",
  "llmParallelism": "increase cautiously if model quota allows"
}
```

---

# Recommended Tuning Loop

Follow this loop for continuous tuning:

1. Keep seed fixed.
2. Record current config.
3. Build communities.
4. Capture detection metrics and community samples.
5. Run fixed evaluation queries.
6. Diagnose broadness, fragmentation, noise, and retrieval misses.
7. Change one primary slider.
8. Rebuild communities.
9. Compare before/after metrics and sample summaries.
10. Keep the change only if it improves retrieval usefulness and summary coherence.

Do not tune from one outlier community. Tune from distribution plus representative samples.

---

# Evaluation Heuristics

Use these as soft targets, not hard rules:

- Largest community: ideally under `80` entities.
- Median community: often useful around `5` to `20` entities.
- Many 2-entity communities: usually resolution too high or summarization threshold too low.
- Many 100+ entity communities: usually resolution too low or co-mention weight too high.
- Singleton count: acceptable if source graph has isolated entities, suspicious if very high.
- Summary quality beats metric purity.

If metrics and summary quality disagree, prefer summary quality for GraphRAG.

---

# Config Output Format

When asked to recommend a tuning change, output both explanation and machine-readable config.

The config must be valid JSON and should include all fields, even unchanged fields.

```json
{
  "communityDetection": {
    "algorithm": "LeidenCpm",
    "directed": false,
    "seed": 42,
    "cpmResolution": 0.25,
    "typedRelationshipWeight": 2.0,
    "coMentionWeight": 1.0,
    "minCommunitySizeToSummarize": 2,
    "maxCommunitiesToSummarize": 50,
    "llmParallelism": 2
  },
  "rationale": {
    "diagnosis": "communities-too-broad",
    "expectedEffect": "Increase resolution to split broad mixed communities into more coherent themes.",
    "changedFields": ["cpmResolution"],
    "risk": "May create more small communities and increase summary count."
  },
  "evaluationPlan": {
    "compareAgainstPrevious": true,
    "metricsToCompare": [
      "totalCommunities",
      "largestCommunitySize",
      "medianCommunitySize",
      "p95CommunitySize",
      "smallCommunityCount",
      "summaryCoherence",
      "retrievalRelevance"
    ],
    "queriesToRun": [
      "Summarize the main themes across all notes.",
      "What changed over time?",
      "What are the top customer/product patterns?"
    ]
  }
}
```

---

# Response Template For A Tuning Agent

Use this structure when producing a recommendation:

````md
## Diagnosis
<short assessment of broadness, fragmentation, hub noise, typed relationship quality, and retrieval impact>

## Recommended Change
<one primary change, optionally one secondary change>

## Why
<explain expected effect in graph/community terms>

## Config
```json
{ ...valid config... }
```

## Validation
<what to compare after rebuilding communities>
````

Prefer small, reversible changes. Preserve the previous config until the new one has been evaluated.

---

# Do Not Do

- Do not optimize only for maximum quality score.
- Do not change multiple sliders at once unless the current config is clearly broken.
- Do not recommend higher resolution only because there are fewer communities than expected.
- Do not recommend lower resolution only because there are many communities.
- Do not ignore generated summary quality.
- Do not use a different seed when comparing resolution or weight changes.
- Do not treat modularity and CPM resolution values as interchangeable.
