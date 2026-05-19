using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using NoteAssistant.KnowledgeGraph.Web.Models;

namespace NoteAssistant.KnowledgeGraph.Web.Controllers;

public class HomeController(IConfiguration configuration) : Controller
{
    public IActionResult Index()
    {
        ViewData["BackendBaseUrl"] = configuration["Backend:BaseUrl"] ?? "http://localhost:5070";
        return View();
    }

    public IActionResult Privacy()
    {
        return View();
    }

    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    public IActionResult Error()
    {
        return View(new ErrorViewModel { RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier });
    }
}
