using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using NoteAssistant.KnowledgeGraph.Web.Models;

namespace NoteAssistant.KnowledgeGraph.Web.Controllers;

public class HomeController(IConfiguration configuration) : Controller
{
    private static readonly HashSet<string> AllowedEntityImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".webp", ".svg"
    };

    public IActionResult Index()
    {
        ViewData["BackendBaseUrl"] = configuration["Backend:BaseUrl"] ?? "http://localhost:5070";
        return View();
    }

    public IActionResult NoteAssistant()
    {
        ViewData["BackendBaseUrl"] = configuration["Backend:BaseUrl"] ?? "http://localhost:5070";
        ViewData["UseFullWidth"] = true;
        return View();
    }

    public IActionResult GraphExplorer()
    {
        ViewData["BackendBaseUrl"] = configuration["Backend:BaseUrl"] ?? "http://localhost:5070";
        ViewData["UseFullWidth"] = true;
        return View();
    }

    public IActionResult Statistics()
    {
        ViewData["BackendBaseUrl"] = configuration["Backend:BaseUrl"] ?? "http://localhost:5070";
        return View();
    }

    public IActionResult DatabaseMgn()
    {
        ViewData["BackendBaseUrl"] = configuration["Backend:BaseUrl"] ?? "http://localhost:5070";
        ViewData["UseFullWidth"] = true;
        return View();
    }

    [HttpGet]
    public IActionResult EntityImages()
    {
        var directory = GetEntityImageDirectory();
        Directory.CreateDirectory(directory);
        var images = Directory.EnumerateFiles(directory)
            .Where(path => AllowedEntityImageExtensions.Contains(Path.GetExtension(path)))
            .OrderBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
            .Select(path => new
            {
                fileName = Path.GetFileName(path),
                url = Url.Content($"~/entity_img/{Path.GetFileName(path)}"),
                size = new FileInfo(path).Length
            })
            .ToList();

        return Json(new { images });
    }

    [HttpPost]
    [RequestSizeLimit(20_000_000)]
    public async Task<IActionResult> UploadEntityImage(IFormFile? file, CancellationToken cancellationToken)
    {
        if (file is null || file.Length == 0)
        {
            return BadRequest(new { error = "Image file is required." });
        }

        var extension = Path.GetExtension(file.FileName);
        if (!AllowedEntityImageExtensions.Contains(extension))
        {
            return BadRequest(new { error = "Supported image types: png, jpg, jpeg, gif, webp, svg." });
        }

        var directory = GetEntityImageDirectory();
        Directory.CreateDirectory(directory);
        var safeName = Path.GetFileNameWithoutExtension(file.FileName);
        safeName = string.Concat(safeName.Select(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' ? ch : '-')).Trim('-');
        if (string.IsNullOrWhiteSpace(safeName))
        {
            safeName = "entity-image";
        }

        var fileName = $"{safeName}-{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}{extension.ToLowerInvariant()}";
        var filePath = Path.Combine(directory, fileName);
        await using (var stream = System.IO.File.Create(filePath))
        {
            await file.CopyToAsync(stream, cancellationToken);
        }

        return Json(new
        {
            fileName,
            url = Url.Content($"~/entity_img/{fileName}"),
            size = file.Length
        });
    }

    public IActionResult Privacy()
    {
        return View();
    }

    private string GetEntityImageDirectory()
        => Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", "entity_img");

    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    public IActionResult Error()
    {
        return View(new ErrorViewModel { RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier });
    }
}
