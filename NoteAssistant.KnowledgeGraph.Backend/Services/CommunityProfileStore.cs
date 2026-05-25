using System.Text.Json;
using System.Globalization;
using NoteAssistant.KnowledgeGraph.Backend.Models;

namespace NoteAssistant.KnowledgeGraph.Backend.Services;

public sealed class CommunityProfileStore
{
    private sealed class StoreDocument
    {
        public string? ActiveProfileId { get; set; }
        public List<CommunityTuningProfile> Profiles { get; set; } = [];
    }

    private readonly string _filePath;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public CommunityProfileStore(IHostEnvironment environment)
    {
        var directory = Path.Combine(environment.ContentRootPath, "cache");
        _filePath = Path.Combine(directory, "community_profiles.json");
    }

    public async Task<CommunityProfileSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var document = await ReadDocumentAsync(cancellationToken);
            return ToSnapshot(document);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<CommunityTuningProfile?> GetActiveProfileAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var document = await ReadDocumentAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(document.ActiveProfileId))
            {
                return null;
            }

            return document.Profiles.FirstOrDefault(profile =>
                string.Equals(profile.Id, document.ActiveProfileId, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<CommunityTuningProfile> SaveProfileAsync(SaveCommunityProfileRequest request, CancellationToken cancellationToken)
    {
        if (request.Config is null)
        {
            throw new ArgumentException("Community profile config is required.", nameof(request));
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var document = await ReadDocumentAsync(cancellationToken);
            var profile = new CommunityTuningProfile(
                Id: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture),
                CreatedAt: DateTimeOffset.UtcNow,
                Config: request.Config,
                ScorePercent: request.ScorePercent,
                ConfidencePercent: request.ConfidencePercent,
                Improvement: request.Improvement,
                Source: string.IsNullOrWhiteSpace(request.Source) ? "manual" : request.Source.Trim());

            document.Profiles.Add(profile);
            if (request.MakeActive)
            {
                document.ActiveProfileId = profile.Id;
            }

            await WriteDocumentAsync(document, cancellationToken);
            return profile;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<CommunityProfileSnapshot> SetActiveProfileAsync(string profileId, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var document = await ReadDocumentAsync(cancellationToken);
            var exists = document.Profiles.Any(profile => string.Equals(profile.Id, profileId, StringComparison.OrdinalIgnoreCase));
            if (!exists)
            {
                throw new InvalidOperationException($"Profile '{profileId}' was not found.");
            }

            document.ActiveProfileId = profileId;
            await WriteDocumentAsync(document, cancellationToken);
            return ToSnapshot(document);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<CommunityProfileSnapshot> DeleteProfileAsync(string profileId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(profileId))
        {
            throw new ArgumentException("Profile id is required.", nameof(profileId));
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var document = await ReadDocumentAsync(cancellationToken);
            var removed = document.Profiles.RemoveAll(profile => string.Equals(profile.Id, profileId, StringComparison.OrdinalIgnoreCase));
            if (removed == 0)
            {
                throw new InvalidOperationException($"Profile '{profileId}' was not found.");
            }

            if (string.Equals(document.ActiveProfileId, profileId, StringComparison.OrdinalIgnoreCase))
            {
                document.ActiveProfileId = document.Profiles
                    .OrderByDescending(profile => profile.CreatedAt)
                    .Select(profile => profile.Id)
                    .FirstOrDefault();
            }

            await WriteDocumentAsync(document, cancellationToken);
            return ToSnapshot(document);
        }
        finally
        {
            _gate.Release();
        }
    }

    private CommunityProfileSnapshot ToSnapshot(StoreDocument document)
    {
        var ordered = document.Profiles
            .OrderByDescending(profile => profile.CreatedAt)
            .ToArray();

        return new CommunityProfileSnapshot(document.ActiveProfileId, ordered);
    }

    private async Task<StoreDocument> ReadDocumentAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_filePath))
        {
            return new StoreDocument();
        }

        await using var stream = File.OpenRead(_filePath);
        var document = await JsonSerializer.DeserializeAsync<StoreDocument>(stream, _jsonOptions, cancellationToken);
        return document ?? new StoreDocument();
    }

    private async Task WriteDocumentAsync(StoreDocument document, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await using var stream = File.Create(_filePath);
        await JsonSerializer.SerializeAsync(stream, document, _jsonOptions, cancellationToken);
    }
}
