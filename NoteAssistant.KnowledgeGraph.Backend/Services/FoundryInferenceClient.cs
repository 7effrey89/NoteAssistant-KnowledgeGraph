using System.ClientModel;
using System.Text.Json;
using Azure.Core;
using Azure.Identity;
using Azure.AI.OpenAI;
using Microsoft.Extensions.Options;
using NoteAssistant.KnowledgeGraph.Backend.Models;
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
    Task<AnswerResult> AnswerQuestionAsync(string question, string context, CancellationToken cancellationToken);
    Task<QuestionAnalysisResult> AnalyzeQuestionAsync(string question, string? clarification, CancellationToken cancellationToken);
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

public sealed class FoundryInferenceClient : IFoundryInferenceClient
{
    private const string CredentialEnvVar = "AZURE_INFERENCE_CREDENTIAL";
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
        "Extract key entities from the user content. Return ONLY a JSON array of objects with 'label' and 'name'. " +
        "Use labels like Company, Product, Platform, Technology, Concept, Person, or Organization. No prose.";
    private readonly FoundryOptions _copilot;
    private readonly ILogger<FoundryInferenceClient> _logger;
    private readonly EmbeddingClient? _embeddingsClient;
    private readonly ChatClient? _chatClient;

    public FoundryInferenceClient(IOptions<FoundryOptions> copilotOptions, ILogger<FoundryInferenceClient> logger)
    {
        _copilot = copilotOptions.Value;
        _logger = logger;

        if (!TryBuildEndpoint(out var endpoint))
        {
            return;
        }

        var apiKey = !string.IsNullOrWhiteSpace(_copilot.ApiKey)
            ? _copilot.ApiKey
            : Environment.GetEnvironmentVariable(CredentialEnvVar);

        AzureOpenAIClient azureClient;
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
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

    public string AnswerSystemPrompt => DefaultAnswerSystemPrompt;

    public string AnalysisSystemPrompt => DefaultAnalysisSystemPrompt;

    public string EntityExtractionSystemPrompt => DefaultEntityExtractionSystemPrompt;

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
        if (!IsConfigured)
        {
            throw new InvalidOperationException("Foundry inference is not configured. Set Copilot settings and credentials.");
        }

        var messages = new List<ChatMessage>
        {
            ChatMessage.CreateSystemMessage(DefaultEntityExtractionSystemPrompt),
            ChatMessage.CreateUserMessage(markdownContent)
        };

        var response = await _chatClient!.CompleteChatAsync(messages, cancellationToken: cancellationToken).ConfigureAwait(false);
        var content = response.Value.Content.Count > 0 ? response.Value.Content[0].Text : string.Empty;

        if (!TryParseEntities(content ?? string.Empty, out var entities))
        {
            _logger.LogWarning("Failed to parse entity JSON from Foundry response.");
            return Array.Empty<EntityDto>();
        }

        return entities;
    }

    public async Task<AnswerResult> AnswerQuestionAsync(string question, string context, CancellationToken cancellationToken)
    {
        if (_chatClient is null)
        {
            throw new InvalidOperationException("Foundry chat is not configured. Set Copilot:ModelDeployment.");
        }

        var messages = new List<ChatMessage>
        {
            ChatMessage.CreateSystemMessage(DefaultAnswerSystemPrompt),
            ChatMessage.CreateUserMessage($"Context:\n{context}\n\nQuestion:\n{question}")
        };

        var response = await _chatClient.CompleteChatAsync(messages, cancellationToken: cancellationToken).ConfigureAwait(false);
        var answer = response.Value.Content.Count > 0 ? response.Value.Content[0].Text ?? string.Empty : string.Empty;
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
            ChatMessage.CreateSystemMessage(DefaultAnalysisSystemPrompt),
            ChatMessage.CreateUserMessage(userPrompt)
        };

        var response = await _chatClient.CompleteChatAsync(messages, cancellationToken: cancellationToken).ConfigureAwait(false);
        var content = response.Value.Content.Count > 0 ? response.Value.Content[0].Text : string.Empty;
        var tokenUsage = ExtractTokenUsage(response.Value.Usage);

        if (!TryParseQuestionAnalysis(content ?? string.Empty, out var result))
        {
            _logger.LogWarning("Failed to parse question analysis JSON from Foundry response.");
            return new QuestionAnalysisResult([], null, question, DefaultAnalysisSystemPrompt, userPrompt, tokenUsage);
        }

        return result with
        {
            SystemPrompt = DefaultAnalysisSystemPrompt,
            UserPrompt = userPrompt,
            TokenUsage = tokenUsage
        };
    }

    private static LlmTokenUsage? ExtractTokenUsage(OpenAI.Chat.ChatTokenUsage? usage)
    {
        if (usage is null)
        {
            return null;
        }

        return new LlmTokenUsage(usage.InputTokenCount, usage.OutputTokenCount);
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
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            foreach (var item in doc.RootElement.EnumerateArray())
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

                entities.Add(new EntityDto(label, name));
            }

            return entities.Count > 0;
        }
        catch (JsonException)
        {
            return false;
        }
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
