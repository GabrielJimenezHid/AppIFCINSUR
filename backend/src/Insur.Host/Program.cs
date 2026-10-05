using Insur.Host;
using Insur.Plugins.Abstractions;
using Microsoft.AspNetCore.Http.Features;

var builder = WebApplication.CreateBuilder(args);

builder.WebHost.UseUrls("http://localhost:5080");
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = 1L * 1024 * 1024 * 1024);
builder.Services.Configure<FormOptions>(o => o.MultipartBodyLengthLimit = 1L * 1024 * 1024 * 1024);

var app = builder.Build();

var pluginsDir = app.Configuration["PluginsDirectory"]
    ?? Path.GetFullPath(Path.Combine(app.Environment.ContentRootPath, "..", "..", "plugins-out"));
var loader = new PluginLoader();
loader.LoadFrom(pluginsDir, app.Logger);

app.MapGet("/api/plugins", () => loader.Plugins.Select(p => p.Manifest));

app.MapPost("/api/plugins/{id}/run", async (string id, HttpRequest request, CancellationToken ct) =>
{
    var plugin = loader.Find(id);
    if (plugin is null) return Results.NotFound(new { error = $"Plugin '{id}' no encontrado" });

    var form = await request.ReadFormAsync(ct);
    var workDir = Path.Combine(Path.GetTempPath(), "ifcplatform", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(workDir);

    try
    {
        var files = new Dictionary<string, string>();
        foreach (var file in form.Files)
        {
            var path = Path.Combine(workDir, Path.GetFileName(file.FileName));
            await using (var stream = File.Create(path)) await file.CopyToAsync(stream, ct);
            files[file.Name] = path;
        }

        if (!files.TryGetValue("ifc", out var ifcPath))
            return Results.BadRequest(new { error = "Falta el archivo IFC (campo 'ifc')" });
        files.Remove("ifc");

        var parameters = form.Where(kv => kv.Key != "action").ToDictionary(kv => kv.Key, kv => kv.Value.ToString());
        var action = form["action"].ToString();
        if (string.IsNullOrEmpty(action)) action = "run";

        var result = await plugin.RunAsync(new PluginContext(ifcPath, action, parameters, files), ct);

        return result switch
        {
            TableResult t => Results.Ok(new { kind = "table", t.Title, t.Summary, t.GroupBy, t.Columns, t.Rows }),
            FileResult f => Results.File(f.Content, f.ContentType, f.FileName),
            ErrorResult e => Results.BadRequest(new { error = e.Message }),
            _ => Results.StatusCode(500)
        };
    }
    catch (Exception ex)
    {
        app.Logger.LogError(ex, "Error ejecutando plugin {Id}", id);
        return Results.Json(new { error = ex.Message }, statusCode: 500);
    }
    finally
    {
        try { Directory.Delete(workDir, recursive: true); } catch { }
    }
});

app.Run();
