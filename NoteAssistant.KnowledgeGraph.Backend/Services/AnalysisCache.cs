using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using NoteAssistant.KnowledgeGraph.Backend.Models;

namespace NoteAssistant.KnowledgeGraph.Backend.Services;

public interface IAnalysisCache
{
    string CacheDirectory { get; }
    string ComputeHash(string content);
    Task<GraphIngestionPlan?> TryGetAsync(string contentHash, CancellationToken cancellationToken);
    Task SaveAsync(GraphIngestionPlan plan, CancellationToken cancellationToken);
}

public sealed class AnalysisCache : IAnalysisCache
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly ILogger<AnalysisCache> _logger;

    public AnalysisCache(IHostEnvironment environment, IOptions<AnalysisCacheOptions> options, ILogger<AnalysisCache> logger)
    {
        _logger = logger;
        var configured = options.Value.Directory;
        CacheDirectory = string.IsNullOrWhiteSpace(configured)
            ? Path.GetFullPath(Path.Combine(environment.ContentRootPath, "..", "cache", "analysis"))
            : Path.GetFullPath(configured);

        Directory.CreateDirectory(CacheDirectory);
    }

    public string CacheDirectory { get; }

    public string ComputeHash(string content)
    {
        var bytes = Encoding.UTF8.GetBytes(content ?? string.Empty);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public async Task<GraphIngestionPlan?> TryGetAsync(string contentHash, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(contentHash))
        {
            return null;
        }

        var match = Directory.EnumerateFiles(CacheDirectory, $"{contentHash[..12]}__*.json")
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();

        if (match is null)
        {
            return null;
        }

        try
        {
            await using var stream = File.OpenRead(match);
            var plan = await JsonSerializer.DeserializeAsync<GraphIngestionPlan>(stream, JsonOptions, cancellationToken);
            if (plan is null)
            {
                return null;
            }

            _logger.LogInformation("Cache hit for hash {Hash} from {File}", contentHash[..12], Path.GetFileName(match));
            return plan with { Cached = true };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read cache file {File}; treating as miss.", match);
            return null;
        }
    }

    public async Task SaveAsync(GraphIngestionPlan plan, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(plan.ContentHash))
        {
            return;
        }

        var safeName = MakeSafeFileName(plan.Status.FileName);
        var timestamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss");
        var fileName = $"{plan.ContentHash[..12]}__{safeName}__{timestamp}.json";
        var path = Path.Combine(CacheDirectory, fileName);

        try
        {
            await using var stream = File.Create(path);
            await JsonSerializer.SerializeAsync(stream, plan with { Cached = false }, JsonOptions, cancellationToken);
            _logger.LogInformation("Cached analysis to {File}", path);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to write analysis cache file {File}.", path);
        }
    }

    private static string MakeSafeFileName(string fileName)
    {
        var name = Path.GetFileNameWithoutExtension(fileName ?? "document");
        if (string.IsNullOrWhiteSpace(name))
        {
            name = "document";
        }

        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Select(c => invalid.Contains(c) || c == ' ' ? '_' : c).ToArray());
        return cleaned.Length > 60 ? cleaned[..60] : cleaned;
    }
}

public sealed class AnalysisCacheOptions
{
    public string? Directory { get; set; }
}
