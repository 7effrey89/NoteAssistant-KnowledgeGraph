using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NoteAssistant.KnowledgeGraph.Backend.Models;
using NoteAssistant.KnowledgeGraph.Backend.Services;
using Xunit;

namespace NoteAssistant.KnowledgeGraph.Backend.Tests;

/// <summary>
/// Verifies that the summarize-transcript endpoint always names the output file after the
/// *original transcript folder*, not after the output folder.
///
/// Bug scenario:
///   originalFolderPath = C:\temp\ISS - Insight - 27-05-26 13.02.16
///   outputFolderPath   = C:\temp\Noteassistant_MeetingSummaryV2
///
///   Wrong file: Noteassistant_MeetingSummaryV2\Noteassistant_MeetingSummaryV2_summary_v2.md
///   Right file: Noteassistant_MeetingSummaryV2\ISS - Insight - 27-05-26 13.02.16_summary_v2.md
/// </summary>
public sealed class TranscriptSummaryFileNameTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public TranscriptSummaryFileNameTests(WebApplicationFactory<Program> factory)
    {
        var cacheDir = Path.Combine(Path.GetTempPath(), "kg-filename-tests", Guid.NewGuid().ToString("N"));
        _factory = factory.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration((_, cfg) =>
            {
                cfg.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["AnalysisCache:Directory"] = cacheDir
                });
            });
            builder.ConfigureServices(services =>
            {
                var toRemove = services
                    .Where(d => d.ServiceType == typeof(IFoundryInferenceClient)
                             || d.ServiceType == typeof(IAgeDatabaseConnectionFactory)
                             || d.ServiceType == typeof(AgeGraphRepository))
                    .ToList();
                foreach (var d in toRemove)
                {
                    services.Remove(d);
                }

                services.AddSingleton<IFoundryInferenceClient>(new FakeFoundryInferenceClient
                {
                    IsConfigured = true
                });

                services.AddSingleton<IAgeDatabaseConnectionFactory>(new StubAgeDatabaseConnectionFactory
                {
                    IsConfigured = false
                });

                services.AddSingleton<AgeGraphRepository>();
            });
        });
    }

    // ---------------------------------------------------------------
    // Happy-path: original folder name drives the output file name
    // ---------------------------------------------------------------

    [Fact]
    public async Task SummarizeTranscript_UsesOriginalFolderLeafName_WhenFullPathProvided()
    {
        // originalFolderPath is a full Windows path; only the leaf segment should be used.
        var outputFolder = TempOutputFolder();
        try
        {
            var client = _factory.CreateClient();
            using var content = BuildContent(
                outputFolderPath: outputFolder,
                originalFolderPath: @"C:\Users\you\Documents\NoteAssistant\ISS - Insight - 27-05-26 13.02.16");

            using var response = await client.PostAsync("/api/noteassistant/summarize-transcript", content);
            var payload = await response.Content.ReadFromJsonAsync<TranscriptSummaryResponse>();

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.NotNull(payload);
            Assert.True(payload!.Success);
            Assert.Equal("ISS - Insight - 27-05-26 13.02.16_summary_v2.md", payload.SuggestedFileName);
        }
        finally { CleanUp(outputFolder); }
    }

    [Fact]
    public async Task SummarizeTranscript_UsesOriginalFolderName_WhenJustNameProvided()
    {
        // originalFolderPath is just a folder name with no path separators.
        var outputFolder = TempOutputFolder();
        try
        {
            var client = _factory.CreateClient();
            using var content = BuildContent(
                outputFolderPath: outputFolder,
                originalFolderPath: "ISS - Insight - 27-05-26 13.02.16");

            using var response = await client.PostAsync("/api/noteassistant/summarize-transcript", content);
            var payload = await response.Content.ReadFromJsonAsync<TranscriptSummaryResponse>();

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.NotNull(payload);
            Assert.Equal("ISS - Insight - 27-05-26 13.02.16_summary_v2.md", payload!.SuggestedFileName);
        }
        finally { CleanUp(outputFolder); }
    }

    [Fact]
    public async Task SummarizeTranscript_OutputPath_UsesOriginalFolderName_NotOutputFolderName()
    {
        // Core regression test for the reported bug:
        //   output folder   = …\Noteassistant_MeetingSummaryV2
        //   original folder = …\ISS - Insight - 27-05-26 13.02.16
        //
        // The file written inside the output folder must be named after the *original* folder.
        var outputFolder = TempOutputFolder("Noteassistant_MeetingSummaryV2");
        try
        {
            var client = _factory.CreateClient();
            using var content = BuildContent(
                outputFolderPath: outputFolder,
                originalFolderPath: @"C:\temp\ISS - Insight - 27-05-26 13.02.16");

            using var response = await client.PostAsync("/api/noteassistant/summarize-transcript", content);
            var payload = await response.Content.ReadFromJsonAsync<TranscriptSummaryResponse>();

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.NotNull(payload);
            Assert.True(payload!.Success);

            // File name must come from the ORIGINAL folder, not the output folder
            Assert.Equal("ISS - Insight - 27-05-26 13.02.16_summary_v2.md", payload.SuggestedFileName);
            Assert.DoesNotContain("Noteassistant_MeetingSummaryV2", payload.SuggestedFileName,
                StringComparison.OrdinalIgnoreCase);

            // OutputPath must end with the correct file name
            Assert.NotNull(payload.OutputPath);
            Assert.EndsWith("ISS - Insight - 27-05-26 13.02.16_summary_v2.md", payload.OutputPath,
                StringComparison.OrdinalIgnoreCase);
        }
        finally { CleanUp(outputFolder); }
    }

    [Fact]
    public async Task SummarizeTranscript_OutputFile_IsPhysicallyWritten_WithCorrectName()
    {
        var outputFolder = TempOutputFolder();
        try
        {
            var client = _factory.CreateClient();
            using var content = BuildContent(
                outputFolderPath: outputFolder,
                originalFolderPath: "Sydbank Session - 31-08-23 13.59.26");

            using var response = await client.PostAsync("/api/noteassistant/summarize-transcript", content);
            var payload = await response.Content.ReadFromJsonAsync<TranscriptSummaryResponse>();

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.NotNull(payload?.OutputPath);
            Assert.True(File.Exists(payload!.OutputPath),
                $"Expected summary file to exist at: {payload.OutputPath}");
            Assert.Equal("Sydbank Session - 31-08-23 13.59.26_summary_v2.md", payload.SuggestedFileName);
        }
        finally { CleanUp(outputFolder); }
    }

    // ---------------------------------------------------------------
    // Error cases
    // ---------------------------------------------------------------

    [Fact]
    public async Task SummarizeTranscript_ReturnsBadRequest_WhenOriginalFolderPathIsEmpty()
    {
        // originalFolderPath is required. Without it the system cannot derive the
        // correct file name and must reject the request rather than silently using
        // the output folder name as a fallback.
        var outputFolder = TempOutputFolder();
        try
        {
            var client = _factory.CreateClient();
            using var content = BuildContent(
                outputFolderPath: outputFolder,
                originalFolderPath: "");

            using var response = await client.PostAsync("/api/noteassistant/summarize-transcript", content);
            var payload = await response.Content.ReadFromJsonAsync<TranscriptSummaryResponse>();

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.NotNull(payload);
            Assert.False(payload!.Success);
            Assert.Contains("original", payload.Error ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        }
        finally { CleanUp(outputFolder); }
    }

    [Fact]
    public async Task SummarizeTranscript_ReturnsBadRequest_WhenOriginalFolderPathIsWhitespace()
    {
        var outputFolder = TempOutputFolder();
        try
        {
            var client = _factory.CreateClient();
            using var content = BuildContent(
                outputFolderPath: outputFolder,
                originalFolderPath: "   ");

            using var response = await client.PostAsync("/api/noteassistant/summarize-transcript", content);

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
        finally { CleanUp(outputFolder); }
    }

    // ---------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------

    private static string TempOutputFolder(string? suffix = null)
    {
        var name = suffix is null
            ? "kg-test-out-" + Guid.NewGuid().ToString("N")
            : $"kg-test-out-{suffix}-{Guid.NewGuid().ToString("N")[..8]}";
        var path = Path.Combine(Path.GetTempPath(), name);
        Directory.CreateDirectory(path);
        return path;
    }

    private static void CleanUp(string folder)
    {
        try { Directory.Delete(folder, recursive: true); }
        catch { /* best-effort */ }
    }

    private static MultipartFormDataContent BuildContent(
        string outputFolderPath,
        string originalFolderPath,
        string transcriptContent = "Transcript: Alice said hello. Bob said hi. 14:00.")
    {
        var content = new MultipartFormDataContent();
        var fileBytes = System.Text.Encoding.UTF8.GetBytes(transcriptContent);
        var fileContent = new ByteArrayContent(fileBytes);
        fileContent.Headers.ContentType =
            new System.Net.Http.Headers.MediaTypeHeaderValue("text/plain");
        content.Add(fileContent, "files", "transcript.txt");
        content.Add(new StringContent(outputFolderPath), "outputFolderPath");
        content.Add(new StringContent(originalFolderPath), "originalFolderPath");
        return content;
    }
}
