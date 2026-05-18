using NoteAssistant.KnowledgeGraph.Backend.Models;
using NoteAssistant.KnowledgeGraph.Backend.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddCors(options =>
{
    options.AddPolicy("Open", policy =>
        policy.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod());
});

builder.Services.AddSingleton<IngestionStore>();
builder.Services.AddSingleton<IMarkdownGraphIngestionService, MarkdownGraphIngestionService>();
builder.Services.AddSingleton<QueryAssistantService>();
builder.Services.AddSingleton<AgeGraphRepository>();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseCors("Open");
app.UseHttpsRedirection();

app.MapPost("/api/documents/upload", async (HttpRequest request, IMarkdownGraphIngestionService ingestionService, IngestionStore store, AgeGraphRepository repository, CancellationToken cancellationToken) =>
{
    if (!request.HasFormContentType)
    {
        return Results.BadRequest(new { error = "Upload must use multipart/form-data." });
    }

    var form = await request.ReadFormAsync(cancellationToken);
    var file = form.Files["file"] ?? form.Files.FirstOrDefault();

    if (file is null || file.Length == 0)
    {
        return Results.BadRequest(new { error = "A markdown file is required." });
    }

    if (!file.FileName.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
    {
        return Results.BadRequest(new { error = "Only .md files are supported." });
    }

    string markdown;
    using (var reader = new StreamReader(file.OpenReadStream()))
    {
        markdown = await reader.ReadToEndAsync(cancellationToken);
    }

    var plan = ingestionService.CreateGraphPlan(file.FileName, markdown);
    store.Upsert(plan.Status with { State = "Queued" });

    if (repository.IsConfigured)
    {
        var execution = await repository.TryExecuteIngestionPlanAsync(plan, cancellationToken);
        var updated = plan.Status with
        {
            State = execution.Success ? "Completed" : "Failed",
            Message = execution.Success ? "Ingested into PostgreSQL/AGE." : execution.ErrorMessage ?? "Ingestion failed."
        };

        store.Upsert(updated);
        return Results.Ok(plan with { Status = updated });
    }

    var completedWithoutDb = plan.Status with
    {
        State = "Ready",
        Message = "Deployment scripts and SQL generated. Configure ConnectionStrings:AgeDatabase to execute directly."
    };

    store.Upsert(completedWithoutDb);
    return Results.Ok(plan with { Status = completedWithoutDb });
})
.DisableAntiforgery();

app.MapGet("/api/documents/{documentId:int}/status", (int documentId, IngestionStore store) =>
{
    var status = store.Get(documentId);
    return status is null
        ? Results.NotFound(new { error = $"No document found for id {documentId}." })
        : Results.Ok(status);
});

app.MapPost("/api/query", async (GraphQueryRequest request, AgeGraphRepository repository, CancellationToken cancellationToken) =>
{
    var response = await repository.ExecuteSelectQueryAsync(request, cancellationToken);
    return response.Success ? Results.Ok(response) : Results.BadRequest(response);
});

app.MapPost("/api/query/assist", (QueryAssistantRequest request, QueryAssistantService assistant) =>
{
    if (string.IsNullOrWhiteSpace(request.Prompt))
    {
        return Results.BadRequest(new { error = "Prompt is required." });
    }

    return Results.Ok(assistant.Suggest(request.Prompt));
});

app.MapGet("/api/deployment", () =>
{
    return Results.Ok(new
    {
        dockerComposePath = "Deployment/docker-compose.yml",
        initSqlPath = "Deployment/init/01-age-init.sql",
        notes = "Run docker compose up in the backend project to provision PostgreSQL + Apache AGE."
    });
});

app.Run();
