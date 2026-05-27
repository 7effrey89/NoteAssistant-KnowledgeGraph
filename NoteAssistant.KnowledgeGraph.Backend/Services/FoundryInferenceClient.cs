using System.ClientModel;
using System.Data;
using System.Text.Json;
using Azure.Core;
using Azure.Identity;
using Azure.AI.OpenAI;
using Microsoft.Extensions.Options;
using NoteAssistant.KnowledgeGraph.Backend.Models;
using Npgsql;
using OpenAI.Chat;
using OpenAI.Embeddings;

namespace NoteAssistant.KnowledgeGraph.Backend.Services;

public sealed class FoundryOptions
{
    public string? FoundryProjectEndpoint { get; init; }
    public string? ModelDeployment { get; init; }
    public string? EmbeddingDeployment { get; init; }
    public string? AgentName { get; init; }
    public string? FoundryModelResourceId { get; init; }
    public string AuthMode { get; init; } = "EntraId";
    public string? ApiKey { get; init; }
    public string? InferenceEndpoint { get; init; }
    public string EntraScope { get; init; } = "https://cognitiveservices.azure.com/.default";
    public string? TenantId { get; init; }
}

public interface IFoundryInferenceClient
{
    bool IsConfigured { get; }
    Task<float[]> CreateEmbeddingAsync(string input, CancellationToken cancellationToken);
    Task<IReadOnlyList<float[]>> CreateEmbeddingsAsync(IReadOnlyList<string> inputs, CancellationToken cancellationToken);
    Task<IReadOnlyList<EntityDto>> ExtractEntitiesAsync(string markdownContent, CancellationToken cancellationToken);
    Task<GraphExtractionDto> ExtractGraphAsync(string markdownContent, CancellationToken cancellationToken);
    Task<AnswerResult> AnswerQuestionAsync(string question, string context, CancellationToken cancellationToken, string agentName = "Answer Agent");
    Task<QuestionAnalysisResult> AnalyzeQuestionAsync(string question, string? clarification, CancellationToken cancellationToken);
    Task<PromptCompletionResult> CompletePromptAsync(string systemPrompt, string userPrompt, string agentName, string operation, CancellationToken cancellationToken);
    string AnswerSystemPrompt { get; }
    string AnalysisSystemPrompt { get; }
    string EntityExtractionSystemPrompt { get; }
}

public sealed record LlmTokenUsage(int? PromptTokens, int? CompletionTokens);

public sealed record AnswerResult(string Answer, LlmTokenUsage? TokenUsage);

public sealed record QuestionAnalysisResult(
    IReadOnlyList<string> Entities,
    string? ClarificationQuestion,
    string RewrittenQuestion,
    string SystemPrompt,
    string UserPrompt,
    LlmTokenUsage? TokenUsage = null);

public sealed record PromptCompletionResult(
    string Content,
    string SystemPrompt,
    string UserPrompt,
    LlmTokenUsage? TokenUsage = null);

public sealed class FoundryInferenceClient : IFoundryInferenceClient
{
    private const string CredentialEnvVar = "AZURE_INFERENCE_CREDENTIAL";
    private const string PromptTemplateFolder = "prompt_template";
    private const string AnswerSystemPromptFileName = "answerAgent_ContextAnswerSystemPrompt.txt";
    private const string AnalysisSystemPromptFileName = "entityDetectionAgent_QuestionAnalysisSystemPrompt.txt";
    private const string LegacyAnalysisSystemPromptFileName = "clarificationAgent_QuestionAnalysisSystemPrompt.txt";
    private const string EntityExtractionSystemPromptFileName = "graphMakerAgent_EntityExtractionSystemPrompt.txt";
    private const string DefaultAnswerSystemPrompt =
        "You answer questions using the provided context. If the context is insufficient, say you do not have enough information.";
    private const string DefaultAnalysisSystemPrompt =
        "You extract entities and resolve ambiguous references in user questions. Return ONLY JSON: " +
        "{\"entities\":[\"...\"],\"clarificationQuestion\":string|null,\"rewrittenQuestion\":string}. " +
        "If the question contains ambiguous pronouns (they/them/it/this/that/these/those) or unclear references, " +
        "set clarificationQuestion to a concise question that disambiguates who/what is referenced. " +
        "When entities contain non-ASCII letters (e.g., Å, Ø, Ö, Æ), include ASCII variants such as AA, O, OE, AE in the entities list. " +
        "If a Clarification is provided, do NOT ask another clarification; use it to rewrite the question. " +
        "If no clarification is needed, use null. " +
        "rewrittenQuestion should be a clearer version of the question with explicit entities when possible.";
    private const string DefaultEntityExtractionSystemPrompt =
        "Extract key entities and typed relationships from the user content. Return ONLY JSON in this shape: " +
        "{\"entities\":[{\"label\":\"Company\",\"name\":\"...\"}],\"relationships\":[{\"sourceName\":\"...\",\"relationship\":\"uses|evaluates|asks_about|partners_with|competes_with|depends_on|mentions|related_to\",\"targetName\":\"...\",\"confidence\":0.0}]}. " +
        "Use entity labels like Customer, Company, Product, Platform, Technology, Service, Concept, Person, or Organization. " +
        "Only include relationships grounded in the text. No prose.";
    private readonly FoundryOptions _copilot;
    private readonly ILogger<FoundryInferenceClient> _logger;
    private readonly IAgeDatabaseConnectionFactory _connectionFactory;
    private readonly EmbeddingClient? _embeddingsClient;
    private readonly ChatClient? _chatClient;
    private readonly string _answerSystemPrompt;
    private readonly string _analysisSystemPrompt;
    private readonly string _entityExtractionSystemPrompt;

    public FoundryInferenceClient(IOptions<FoundryOptions> copilotOptions, ILogger<FoundryInferenceClient> logger, IHostEnvironment environment, IAgeDatabaseConnectionFactory connectionFactory)
    {
        _copilot = copilotOptions.Value;
        _logger = logger;
        _connectionFactory = connectionFactory;
        _answerSystemPrompt = LoadPromptTemplate(environment, AnswerSystemPromptFileName, DefaultAnswerSystemPrompt);
        _analysisSystemPrompt = LoadPromptTemplate(environment, AnalysisSystemPromptFileName, LegacyAnalysisSystemPromptFileName, DefaultAnalysisSystemPrompt);
        _entityExtractionSystemPrompt = LoadPromptTemplate(environment, EntityExtractionSystemPromptFileName, DefaultEntityExtractionSystemPrompt);

        if (!TryBuildEndpoint(out var endpoint))
        {
            return;
        }

        AzureOpenAIClient azureClient;
        if (IsApiKeyAuthMode(_copilot.AuthMode))
        {
            var apiKey = !string.IsNullOrWhiteSpace(_copilot.ApiKey)
                ? _copilot.ApiKey
                : Environment.GetEnvironmentVariable(CredentialEnvVar);
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                _logger.LogWarning("Foundry ApiKey auth mode is configured, but no API key was provided in Copilot:ApiKey or {CredentialEnvVar}.", CredentialEnvVar);
                return;
            }

            azureClient = new AzureOpenAIClient(endpoint, new ApiKeyCredential(apiKey));
        }
        else
        {
            var tokenCredential = CreateCredential(_copilot);
            azureClient = new AzureOpenAIClient(endpoint, tokenCredential);
        }

        if (!string.IsNullOrWhiteSpace(_copilot.EmbeddingDeployment))
        {
            _embeddingsClient = azureClient.GetEmbeddingClient(_copilot.EmbeddingDeployment);
        }

        if (!string.IsNullOrWhiteSpace(_copilot.ModelDeployment))
        {
            _chatClient = azureClient.GetChatClient(_copilot.ModelDeployment);
        }
    }

    public bool IsConfigured
        => _embeddingsClient is not null
           && _chatClient is not null
           && !string.IsNullOrWhiteSpace(_copilot.EmbeddingDeployment)
           && !string.IsNullOrWhiteSpace(_copilot.ModelDeployment);

    public string AnswerSystemPrompt => _answerSystemPrompt;

    public string AnalysisSystemPrompt => _analysisSystemPrompt;

    public string EntityExtractionSystemPrompt => _entityExtractionSystemPrompt;

    public async Task<float[]> CreateEmbeddingAsync(string input, CancellationToken cancellationToken)
    {
        var embeddings = await CreateEmbeddingsAsync([input], cancellationToken);
        return embeddings[0];
    }

    public async Task<IReadOnlyList<float[]>> CreateEmbeddingsAsync(IReadOnlyList<string> inputs, CancellationToken cancellationToken)
    {
        if (!IsConfigured)
        {
            throw new InvalidOperationException("Foundry inference is not configured. Set Copilot settings and credentials.");
        }

        var response = await _embeddingsClient!.GenerateEmbeddingsAsync(inputs, cancellationToken: cancellationToken).ConfigureAwait(false);
        var result = new List<float[]>(response.Value.Count);
        foreach (var item in response.Value)
        {
            result.Add(item.ToFloats().ToArray());
        }

        return result;
    }

    public async Task<IReadOnlyList<EntityDto>> ExtractEntitiesAsync(string markdownContent, CancellationToken cancellationToken)
    {
        var extraction = await ExtractGraphAsync(markdownContent, cancellationToken).ConfigureAwait(false);
        return extraction.Entities;
    }

    public async Task<GraphExtractionDto> ExtractGraphAsync(string markdownContent, CancellationToken cancellationToken)
    {
        if (!IsConfigured)
        {
            throw new InvalidOperationException("Foundry inference is not configured. Set Copilot settings and credentials.");
        }

        var messages = new List<ChatMessage>
        {
            ChatMessage.CreateSystemMessage(_entityExtractionSystemPrompt),
            ChatMessage.CreateUserMessage(markdownContent)
        };

        var response = await _chatClient!.CompleteChatAsync(messages, cancellationToken: cancellationToken).ConfigureAwait(false);
        var content = response.Value.Content.Count > 0 ? response.Value.Content[0].Text : string.Empty;
        await LogTokenUsageAsync("Graph Maker Agent", "extract-graph", response.Value.Usage, cancellationToken).ConfigureAwait(false);

        if (!TryParseGraphExtraction(content ?? string.Empty, out var extraction))
        {
            _logger.LogWarning("Failed to parse graph extraction JSON from Foundry response.");
            return new GraphExtractionDto(Array.Empty<EntityDto>(), Array.Empty<RelationshipDto>());
        }

        return extraction;
    }

    public async Task<AnswerResult> AnswerQuestionAsync(string question, string context, CancellationToken cancellationToken, string agentName = "Answer Agent")
    {
        if (_chatClient is null)
        {
            throw new InvalidOperationException("Foundry chat is not configured. Set Copilot:ModelDeployment.");
        }

        var messages = new List<ChatMessage>
        {
            ChatMessage.CreateSystemMessage(_answerSystemPrompt),
            ChatMessage.CreateUserMessage($"Context:\n{context}\n\nQuestion:\n{question}")
        };

        var response = await _chatClient.CompleteChatAsync(messages, cancellationToken: cancellationToken).ConfigureAwait(false);
        var answer = response.Value.Content.Count > 0 ? response.Value.Content[0].Text ?? string.Empty : string.Empty;
        await LogTokenUsageAsync(agentName, "answer-question", response.Value.Usage, cancellationToken).ConfigureAwait(false);
        return new AnswerResult(answer, ExtractTokenUsage(response.Value.Usage));
    }

    public async Task<QuestionAnalysisResult> AnalyzeQuestionAsync(string question, string? clarification, CancellationToken cancellationToken)
    {
        if (_chatClient is null)
        {
            throw new InvalidOperationException("Foundry chat is not configured. Set Copilot:ModelDeployment.");
        }

        var clarificationText = string.IsNullOrWhiteSpace(clarification) ? "" : $"\nClarification: {clarification}";
        var userPrompt = $"Question: {question}{clarificationText}";
        var messages = new List<ChatMessage>
        {
            ChatMessage.CreateSystemMessage(_analysisSystemPrompt),
            ChatMessage.CreateUserMessage(userPrompt)
        };

        var response = await _chatClient.CompleteChatAsync(messages, cancellationToken: cancellationToken).ConfigureAwait(false);
        var content = response.Value.Content.Count > 0 ? response.Value.Content[0].Text : string.Empty;
        var tokenUsage = ExtractTokenUsage(response.Value.Usage);
        await LogTokenUsageAsync("Question Analysis Agent", "analyze-question", response.Value.Usage, cancellationToken).ConfigureAwait(false);

        if (!TryParseQuestionAnalysis(content ?? string.Empty, out var result))
        {
            _logger.LogWarning("Failed to parse question analysis JSON from Foundry response.");
            return new QuestionAnalysisResult([], null, question, _analysisSystemPrompt, userPrompt, tokenUsage);
        }

        return result with
        {
            SystemPrompt = _analysisSystemPrompt,
            UserPrompt = userPrompt,
            TokenUsage = tokenUsage
        };
    }

    public async Task<PromptCompletionResult> CompletePromptAsync(
        string systemPrompt,
        string userPrompt,
        string agentName,
        string operation,
        CancellationToken cancellationToken)
    {
        if (_chatClient is null)
        {
            throw new InvalidOperationException("Foundry chat is not configured. Set Copilot:ModelDeployment.");
        }

        var effectiveSystemPrompt = string.IsNullOrWhiteSpace(systemPrompt)
            ? _analysisSystemPrompt
            : systemPrompt;
        var effectiveUserPrompt = userPrompt ?? string.Empty;

        var messages = new List<ChatMessage>
        {
            ChatMessage.CreateSystemMessage(effectiveSystemPrompt),
            ChatMessage.CreateUserMessage(effectiveUserPrompt)
        };

        var response = await _chatClient.CompleteChatAsync(messages, cancellationToken: cancellationToken).ConfigureAwait(false);
        var content = response.Value.Content.Count > 0 ? response.Value.Content[0].Text ?? string.Empty : string.Empty;
        var tokenUsage = ExtractTokenUsage(response.Value.Usage);
        await LogTokenUsageAsync(agentName, operation, response.Value.Usage, cancellationToken).ConfigureAwait(false);
        return new PromptCompletionResult(content, effectiveSystemPrompt, effectiveUserPrompt, tokenUsage);
    }

    private string LoadPromptTemplate(IHostEnvironment environment, string fileName, string fallback)
        => LoadPromptTemplate(environment, fileName, legacyFileName: null, fallback);

    private string LoadPromptTemplate(IHostEnvironment environment, string fileName, string? legacyFileName, string fallback)
    {
        var path = Path.Combine(environment.ContentRootPath, PromptTemplateFolder, fileName);
        try
        {
            if (!File.Exists(path))
            {
                if (!string.IsNullOrWhiteSpace(legacyFileName))
                {
                    var legacyPath = Path.Combine(environment.ContentRootPath, PromptTemplateFolder, legacyFileName);
                    if (File.Exists(legacyPath))
                    {
                        var legacyPrompt = File.ReadAllText(legacyPath).Trim();
                        if (!string.IsNullOrWhiteSpace(legacyPrompt))
                        {
                            _logger.LogWarning("Prompt template {PromptTemplatePath} was not found. Using legacy prompt template {LegacyPromptTemplatePath}.", path, legacyPath);
                            return legacyPrompt;
                        }
                    }
                }

                _logger.LogWarning("Prompt template {PromptTemplatePath} was not found. Using built-in fallback.", path);
                return fallback;
            }

            var prompt = File.ReadAllText(path).Trim();
            if (string.IsNullOrWhiteSpace(prompt))
            {
                _logger.LogWarning("Prompt template {PromptTemplatePath} is empty. Using built-in fallback.", path);
                return fallback;
            }

            return prompt;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read prompt template {PromptTemplatePath}. Using built-in fallback.", path);
            return fallback;
        }
    }

    private static LlmTokenUsage? ExtractTokenUsage(OpenAI.Chat.ChatTokenUsage? usage)
    {
        if (usage is null)
        {
            return null;
        }

        return new LlmTokenUsage(usage.InputTokenCount, usage.OutputTokenCount);
    }

    private async Task LogTokenUsageAsync(string agentName, string operation, OpenAI.Chat.ChatTokenUsage? usage, CancellationToken cancellationToken)
    {
        if (usage is null || !_connectionFactory.IsConfigured)
        {
            return;
        }

        try
        {
            await using var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
            const string schemaSql = """
                                     CREATE SCHEMA IF NOT EXISTS "global";
                                     CREATE TABLE IF NOT EXISTS "global".llm_token_usage (
                                         id BIGSERIAL PRIMARY KEY,
                                         occurred_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                                         agent TEXT NOT NULL,
                                         operation TEXT NOT NULL,
                                         model_deployment TEXT NULL,
                                         prompt_tokens INTEGER NULL,
                                         completion_tokens INTEGER NULL,
                                         total_tokens INTEGER NULL
                                     );
                                     CREATE INDEX IF NOT EXISTS idx_llm_token_usage_occurred_at ON "global".llm_token_usage(occurred_at);
                                     CREATE INDEX IF NOT EXISTS idx_llm_token_usage_agent ON "global".llm_token_usage(agent);
                                     """;
            await using (var schemaCommand = new NpgsqlCommand(schemaSql, connection))
            {
                await schemaCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            const string insertSql = """
                                     INSERT INTO "global".llm_token_usage(agent, operation, model_deployment, prompt_tokens, completion_tokens, total_tokens)
                                     VALUES (@agent, @operation, @model_deployment, @prompt_tokens, @completion_tokens, @total_tokens);
                                     """;
            await using var command = new NpgsqlCommand(insertSql, connection) { CommandType = CommandType.Text };
            command.Parameters.AddWithValue("agent", agentName);
            command.Parameters.AddWithValue("operation", operation);
            command.Parameters.AddWithValue("model_deployment", string.IsNullOrWhiteSpace(_copilot.ModelDeployment) ? DBNull.Value : _copilot.ModelDeployment);
            command.Parameters.AddWithValue("prompt_tokens", usage.InputTokenCount);
            command.Parameters.AddWithValue("completion_tokens", usage.OutputTokenCount);
            command.Parameters.AddWithValue("total_tokens", usage.TotalTokenCount);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to log token usage for {AgentName} {Operation}.", agentName, operation);
        }
    }

    private bool TryBuildEndpoint(out Uri endpoint)
    {
        var endpointValue = _copilot.InferenceEndpoint;
        if (string.IsNullOrWhiteSpace(endpointValue) && !string.IsNullOrWhiteSpace(_copilot.FoundryProjectEndpoint))
        {
            if (Uri.TryCreate(_copilot.FoundryProjectEndpoint, UriKind.Absolute, out var projectEndpoint))
            {
                // Azure.AI.OpenAI talks to the resource-level Azure OpenAI endpoint, e.g.:
                //   https://<resource>.openai.azure.com/
                // Derive it from the Foundry project endpoint host (which is
                // <resource>.services.ai.azure.com).
                var host = projectEndpoint.Host;
                if (host.EndsWith(".services.ai.azure.com", StringComparison.OrdinalIgnoreCase))
                {
                    var resourceName = host[..host.IndexOf('.')];
                    endpointValue = $"https://{resourceName}.openai.azure.com/";
                }
                else
                {
                    endpointValue = $"{projectEndpoint.Scheme}://{host}/";
                }
            }
        }

        if (!Uri.TryCreate(endpointValue, UriKind.Absolute, out endpoint!))
        {
            _logger.LogWarning("Foundry inference endpoint is not configured.");
            endpoint = null!;
            return false;
        }

        return true;
    }

    private static TokenCredential CreateCredential(FoundryOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.TenantId))
        {
            return new DefaultAzureCredential();
        }

        var credentialOptions = new DefaultAzureCredentialOptions
        {
            TenantId = options.TenantId
        };

        return new DefaultAzureCredential(credentialOptions);
    }

    private static bool IsApiKeyAuthMode(string? authMode)
        => string.Equals(authMode, "ApiKey", StringComparison.OrdinalIgnoreCase)
           || string.Equals(authMode, "Key", StringComparison.OrdinalIgnoreCase);

    private static bool TryParseGraphExtraction(string content, out GraphExtractionDto extraction)
    {
        extraction = new GraphExtractionDto(Array.Empty<EntityDto>(), Array.Empty<RelationshipDto>());
        if (string.IsNullOrWhiteSpace(content))
        {
            return false;
        }

        var objectJson = ExtractJsonObject(content);
        if (!string.IsNullOrWhiteSpace(objectJson))
        {
            try
            {
                using var doc = JsonDocument.Parse(objectJson);
                if (doc.RootElement.ValueKind == JsonValueKind.Object)
                {
                    var entities = doc.RootElement.TryGetProperty("entities", out var entitiesValue)
                        ? ParseEntities(entitiesValue)
                        : [];
                    var relationships = doc.RootElement.TryGetProperty("relationships", out var relationshipsValue)
                        ? ParseRelationships(relationshipsValue)
                        : [];
                    extraction = new GraphExtractionDto(entities, relationships);
                    return entities.Count > 0 || relationships.Count > 0;
                }
            }
            catch (JsonException)
            {
                // Fall through to the legacy array parser.
            }
        }

        if (TryParseEntities(content, out var legacyEntities))
        {
            extraction = new GraphExtractionDto(legacyEntities, Array.Empty<RelationshipDto>());
            return true;
        }

        return false;
    }

    private static bool TryParseEntities(string content, out List<EntityDto> entities)
    {
        entities = new List<EntityDto>();
        if (string.IsNullOrWhiteSpace(content))
        {
            return false;
        }

        var json = ExtractJsonArray(content);
        if (string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            entities = ParseEntities(doc.RootElement).ToList();

            return entities.Count > 0;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static IReadOnlyList<EntityDto> ParseEntities(JsonElement value)
    {
        var entities = new List<EntityDto>();
        if (value.ValueKind != JsonValueKind.Array)
        {
            return entities;
        }

        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var label = item.TryGetProperty("label", out var labelValue) ? labelValue.GetString() : null;
            var name = item.TryGetProperty("name", out var nameValue) ? nameValue.GetString() : null;

            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            label = string.IsNullOrWhiteSpace(label) ? "Concept" : label.Trim();
            name = name.Trim();

            if (name.Length is < 2 or > 80)
            {
                continue;
            }

            if (!entities.Any(entity => string.Equals(entity.Label, label, StringComparison.OrdinalIgnoreCase)
                                        && string.Equals(entity.Name, name, StringComparison.OrdinalIgnoreCase)))
            {
                entities.Add(new EntityDto(label, name));
            }
        }

        return entities;
    }

    private static IReadOnlyList<RelationshipDto> ParseRelationships(JsonElement value)
    {
        var relationships = new List<RelationshipDto>();
        if (value.ValueKind != JsonValueKind.Array)
        {
            return relationships;
        }

        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var source = ReadStringProperty(item, "sourceName", "source", "head", "from");
            var relation = ReadStringProperty(item, "relationship", "relation", "type", "label");
            var target = ReadStringProperty(item, "targetName", "target", "tail", "to");
            if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(target))
            {
                continue;
            }

            relation = string.IsNullOrWhiteSpace(relation) ? "related_to" : NormalizeRelationshipType(relation);
            var confidence = ReadDoubleProperty(item, "confidence", "score");
            relationships.Add(new RelationshipDto(source.Trim(), relation, target.Trim(), confidence));
        }

        return relationships
            .Where(r => r.SourceName.Length <= 80 && r.TargetName.Length <= 80 && r.Relationship.Length <= 80)
            .DistinctBy(r => $"{r.SourceName}\t{r.Relationship}\t{r.TargetName}", StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string? ReadStringProperty(JsonElement item, params string[] names)
    {
        foreach (var name in names)
        {
            if (item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
            {
                return value.GetString();
            }
        }

        return null;
    }

    private static double? ReadDoubleProperty(JsonElement item, params string[] names)
    {
        foreach (var name in names)
        {
            if (!item.TryGetProperty(name, out var value))
            {
                continue;
            }

            if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number))
            {
                return number;
            }
        }

        return null;
    }

    private static string NormalizeRelationshipType(string value)
    {
        var normalized = new string(value.Trim().ToLowerInvariant().Select(ch => char.IsLetterOrDigit(ch) ? ch : '_').ToArray());
        while (normalized.Contains("__", StringComparison.Ordinal))
        {
            normalized = normalized.Replace("__", "_", StringComparison.Ordinal);
        }

        return normalized.Trim('_');
    }

    private static string ExtractJsonArray(string content)
    {
        var start = content.IndexOf('[', StringComparison.Ordinal);
        var end = content.LastIndexOf(']');
        if (start < 0 || end <= start)
        {
            return string.Empty;
        }

        return content.Substring(start, end - start + 1);
    }

    private static string ExtractJsonObject(string content)
    {
        var start = content.IndexOf('{');
        var end = content.LastIndexOf('}');
        if (start < 0 || end <= start)
        {
            return string.Empty;
        }

        return content.Substring(start, end - start + 1);
    }

    private static bool TryParseQuestionAnalysis(string content, out QuestionAnalysisResult result)
    {
        result = new QuestionAnalysisResult([], null, string.Empty, string.Empty, string.Empty);
        if (string.IsNullOrWhiteSpace(content))
        {
            return false;
        }

        var jsonStart = content.IndexOf('{');
        var jsonEnd = content.LastIndexOf('}');
        if (jsonStart < 0 || jsonEnd <= jsonStart)
        {
            return false;
        }

        var json = content.Substring(jsonStart, jsonEnd - jsonStart + 1);

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            var entities = new List<string>();
            if (root.TryGetProperty("entities", out var entitiesValue) && entitiesValue.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in entitiesValue.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String)
                    {
                        var name = item.GetString();
                        if (!string.IsNullOrWhiteSpace(name))
                        {
                            entities.Add(name.Trim());
                        }
                    }
                }
            }

            var clarification = root.TryGetProperty("clarificationQuestion", out var clarificationValue)
                ? clarificationValue.GetString()
                : null;
            if (string.IsNullOrWhiteSpace(clarification))
            {
                clarification = null;
            }

            var rewritten = root.TryGetProperty("rewrittenQuestion", out var rewrittenValue)
                ? rewrittenValue.GetString()
                : null;
            rewritten = string.IsNullOrWhiteSpace(rewritten) ? string.Empty : rewritten.Trim();

            result = new QuestionAnalysisResult(entities, clarification, rewritten, string.Empty, string.Empty);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
