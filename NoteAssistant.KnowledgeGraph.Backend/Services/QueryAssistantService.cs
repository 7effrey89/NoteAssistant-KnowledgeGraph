using System.Text.Json;
using System.Text.RegularExpressions;
using NoteAssistant.KnowledgeGraph.Backend.Models;

namespace NoteAssistant.KnowledgeGraph.Backend.Services;

public sealed class QueryAssistantService
{
    private const string PromptTemplateFolder = "prompt_template";
    private const string PromptsFolder = "Prompts";
    private const string CypherSystemPromptFileName = "cypherQueryAgent_SystemPrompt.txt";
    private const string CypherSkillsFileName = "cypher_skills.md";
    private const int MaxValidationAttempts = 4;
    private const string DefaultCypherSystemPrompt =
        "You are the Cypher Query Agent. Generate read-only Cypher for graph exploration. Return ONLY JSON with suggestedCypher and explanation.";
    private readonly IFoundryInferenceClient? _foundry;
    private readonly AgeGraphRepository? _repository;
    private readonly ILogger<QueryAssistantService>? _logger;
    private readonly string _systemPrompt;
    private readonly string _cypherSkills;

    public QueryAssistantService()
    {
        _systemPrompt = DefaultCypherSystemPrompt;
        _cypherSkills = string.Empty;
    }

    public QueryAssistantService(IFoundryInferenceClient foundry, AgeGraphRepository repository, IHostEnvironment environment, ILogger<QueryAssistantService> logger)
    {
        _foundry = foundry;
        _repository = repository;
        _logger = logger;
        _systemPrompt = LoadTextFile(Path.Combine(environment.ContentRootPath, PromptTemplateFolder, CypherSystemPromptFileName), DefaultCypherSystemPrompt);
        _cypherSkills = LoadTextFile(Path.Combine(environment.ContentRootPath, PromptsFolder, CypherSkillsFileName), string.Empty);
    }

    public async Task<QueryAssistantResponse> SuggestAsync(string prompt, CancellationToken cancellationToken)
    {
        if (_foundry?.IsConfigured != true)
        {
            throw new InvalidOperationException("Foundry is not configured for LLM query generation.");
        }

        var userPrompt = BuildUserPrompt(prompt);
        var completion = await _foundry.CompletePromptAsync(
            _systemPrompt,
            userPrompt,
            "Cypher Query Agent",
            "suggest-cypher-query",
            cancellationToken).ConfigureAwait(false);

        if (TryParseLlmResponse(completion.Content, out var response))
        {
            var current = response;
            var validationErrors = new List<string>();
            for (var attempt = 1; attempt <= MaxValidationAttempts; attempt++)
            {
                var validation = await ValidateSuggestedCypherAsync(current.SuggestedCypher, cancellationToken).ConfigureAwait(false);
                if (validation.Success)
                {
                    return current;
                }

                var validationError = validation.Error ?? "Cypher validation failed.";
                validationErrors.Add(validationError);
                _logger?.LogWarning("Cypher Query Agent generated invalid Cypher on attempt {Attempt}/{MaxAttempts}. Error: {ValidationError}", attempt, MaxValidationAttempts, validationError);
                if (attempt == MaxValidationAttempts)
                {
                    break;
                }

                var repaired = await TryRepairAsync(prompt, current, validationErrors, cancellationToken).ConfigureAwait(false);
                if (repaired is null)
                {
                    break;
                }

                current = repaired;
            }

            var fallback = Suggest(prompt);
            var fallbackValidation = await ValidateSuggestedCypherAsync(fallback.SuggestedCypher, cancellationToken).ConfigureAwait(false);
            if (fallbackValidation.Success)
            {
                _logger?.LogWarning("Returning deterministic fallback after LLM Cypher validation failed. Errors: {ValidationErrors}", string.Join(" | ", validationErrors));
                return fallback with
                {
                    Explanation = $"Fallback query returned because LLM-generated Cypher failed validation. {fallback.Explanation}"
                };
            }

            var lastError = validationErrors.Count > 0 ? validationErrors[^1] : fallbackValidation.Error;
            throw new InvalidOperationException($"Cypher Query Agent could not produce a validated query. Last validation error: {lastError}");
        }

        _logger?.LogWarning("Failed to parse Cypher Query Agent response. Falling back to deterministic query assistant.");
        return Suggest(prompt);
    }

    public QueryAssistantResponse Suggest(string prompt)
    {
        var value = prompt.Trim();
        var limit = TryExtractLimit(value);

        var related = TryBuildRelatedQuery(value, limit);
        if (related is not null)
        {
            return related;
        }

        if (value.Contains("document", StringComparison.OrdinalIgnoreCase) && value.Contains("chunk", StringComparison.OrdinalIgnoreCase))
        {
            return new QueryAssistantResponse(
                $"MATCH (d:Document)-[r:HAS_CHUNK]->(c:Chunk)\nRETURN d, r, c\nORDER BY c.id\nLIMIT {limit ?? 50}",
                "Returns documents with their chunks and the connecting relationships for graph exploration.");
        }

        if (value.Contains("entity", StringComparison.OrdinalIgnoreCase) || value.Contains("mentions", StringComparison.OrdinalIgnoreCase))
        {
            return new QueryAssistantResponse(
                $"MATCH (c:Chunk)\nOPTIONAL MATCH (c)-[r:MENTIONS]->(e)\nRETURN c, r, e\nORDER BY c.id\nLIMIT {limit ?? 50}",
                "Shows chunks and any mentioned entities; still returns chunk nodes even when no mentions exist.");
        }

        return new QueryAssistantResponse(
            $"MATCH p=(n)-[r]->(m)\nRETURN n, r, m\nLIMIT {limit ?? 50}",
            "General-purpose graph exploration query that powers the visual explorer.");
    }

    private static QueryAssistantResponse? TryBuildRelatedQuery(string prompt, int? limit)
    {
        var match = Regex.Match(prompt, @"\brelated to\s+(.+)$", RegexOptions.IgnoreCase);
        if (!match.Success)
        {
            match = Regex.Match(prompt, @"\babout\s+(.+)$", RegexOptions.IgnoreCase);
        }

        if (!match.Success)
        {
            return null;
        }

        var entity = match.Groups[1].Value.Trim();
        if (string.IsNullOrWhiteSpace(entity))
        {
            return null;
        }

        var safe = EscapeCypherLiteral(entity);
        return new QueryAssistantResponse(
            $"MATCH (n)-[r]-(m)\nWHERE n.name = '{safe}'\nRETURN n, r, m\nLIMIT {limit ?? 50}",
            $"Shows nodes related to '{entity}' and their immediate relationships.");
    }

    private static int? TryExtractLimit(string prompt)
    {
        var match = Regex.Match(prompt, @"\blimit(?:\s+by|\s+to)?\s+(\d{1,4})\b", RegexOptions.IgnoreCase);
        if (!match.Success)
        {
            match = Regex.Match(prompt, @"\btop\s+(\d{1,4})\b", RegexOptions.IgnoreCase);
        }

        if (!match.Success || !int.TryParse(match.Groups[1].Value, out var limit))
        {
            return null;
        }

        return Math.Clamp(limit, 1, 200);
    }

    private static string EscapeCypherLiteral(string value)
        => value.Replace("'", "''", StringComparison.Ordinal);

    private string BuildUserPrompt(string prompt)
        => string.Join(Environment.NewLine,
        [
            "Use the following Cypher skill/context document when generating the query.",
            "The graph explorer expects the Cypher query alone in suggestedCypher and an explanation in explanation.",
            string.Empty,
            "# Cypher skill context",
            string.IsNullOrWhiteSpace(_cypherSkills) ? "No cypher_skills.md content was available." : _cypherSkills,
            string.Empty,
            "# User request",
            prompt.Trim(),
            string.Empty,
            "Return strict JSON only: {\"suggestedCypher\":\"MATCH ...\",\"explanation\":\"...\"}"
        ]);

    private string BuildRepairPrompt(string prompt, QueryAssistantResponse failedResponse, IReadOnlyList<string> validationErrors)
        => string.Join(Environment.NewLine,
        [
            BuildUserPrompt(prompt),
            string.Empty,
            "# Previous generated query failed validation",
            failedResponse.SuggestedCypher,
            string.Empty,
            "# Validation errors from Apache AGE/PostgreSQL",
            string.Join(Environment.NewLine, validationErrors.Select((error, index) => $"{index + 1}. {error}")),
            string.Empty,
            "Apache AGE corrections to apply:",
            "- Avoid path variables such as MATCH p = ... RETURN p.",
            "- Avoid toString() on nodes, relationships, paths, maps, and lists.",
            "- Return explicit graph triples such as RETURN n, r, m.",
            "- Keep WHERE directly after the MATCH clause it filters.",
            string.Empty,
            "Generate a corrected read-only Apache AGE compatible Cypher query. Return strict JSON only."
        ]);

    private async Task<QueryAssistantResponse?> TryRepairAsync(string prompt, QueryAssistantResponse failedResponse, IReadOnlyList<string> validationErrors, CancellationToken cancellationToken)
    {
        if (_foundry is null)
        {
            return null;
        }

        var repairCompletion = await _foundry.CompletePromptAsync(
            _systemPrompt,
            BuildRepairPrompt(prompt, failedResponse, validationErrors),
            "Cypher Query Agent",
            "repair-cypher-query",
            cancellationToken).ConfigureAwait(false);

        if (!TryParseLlmResponse(repairCompletion.Content, out var repaired))
        {
            _logger?.LogWarning("Failed to parse Cypher Query Agent repair response.");
            return null;
        }

        return repaired;
    }

    private async Task<(bool Success, string? Error)> ValidateSuggestedCypherAsync(string cypher, CancellationToken cancellationToken)
    {
        if (_repository is null)
        {
            return (true, null);
        }

        return await _repository.ValidateReadOnlyCypherAsync(cypher, "knowledge_graph", cancellationToken).ConfigureAwait(false);
    }

    internal static bool TryParseLlmResponse(string content, out QueryAssistantResponse response)
    {
        response = new QueryAssistantResponse(string.Empty, string.Empty);
        var json = ExtractJsonObject(content);
        if (string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (!root.TryGetProperty("suggestedCypher", out var cypherElement) || cypherElement.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            var cypher = cypherElement.GetString()?.Trim();
            if (string.IsNullOrWhiteSpace(cypher))
            {
                return false;
            }

            var explanation = root.TryGetProperty("explanation", out var explanationElement) && explanationElement.ValueKind == JsonValueKind.String
                ? explanationElement.GetString()?.Trim()
                : "Generated by the Cypher Query Agent.";

            response = new QueryAssistantResponse(cypher, string.IsNullOrWhiteSpace(explanation) ? "Generated by the Cypher Query Agent." : explanation!);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string? ExtractJsonObject(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return null;
        }

        var trimmed = content.Trim();
        if (trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            trimmed = Regex.Replace(trimmed, "^```(?:json)?\\s*", string.Empty, RegexOptions.IgnoreCase).Trim();
            trimmed = Regex.Replace(trimmed, "\\s*```$", string.Empty).Trim();
        }

        var start = trimmed.IndexOf('{');
        var end = trimmed.LastIndexOf('}');
        return start >= 0 && end > start ? trimmed[start..(end + 1)] : null;
    }

    private static string LoadTextFile(string path, string fallback)
    {
        try
        {
            if (!File.Exists(path))
            {
                return fallback;
            }

            var text = File.ReadAllText(path).Trim();
            return string.IsNullOrWhiteSpace(text) ? fallback : text;
        }
        catch
        {
            return fallback;
        }
    }
}
