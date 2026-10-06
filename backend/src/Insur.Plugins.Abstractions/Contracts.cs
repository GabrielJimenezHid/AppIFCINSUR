namespace Insur.Plugins.Abstractions;

public interface IIfcPlugin
{
    PluginManifest Manifest { get; }

    Task<PluginResult> RunAsync(PluginContext context, CancellationToken cancellationToken);
}

public sealed record PluginManifest(
    string Id,
    string Name,
    string Version,
    string Description,
    IReadOnlyList<PluginParameter> Parameters,
    IReadOnlyList<PluginAction> Actions);

public sealed record PluginParameter(
    string Name,
    string Label,
    string Type,
    string? Default = null,
    bool Required = false);

public sealed record PluginAction(string Id, string Label);

public sealed record PluginContext(
    string IfcPath,
    string Action,
    IReadOnlyDictionary<string, string> Parameters,
    IReadOnlyDictionary<string, string> Files);

public abstract record PluginResult;

/// <summary>
/// Type: "text", "number" o "select". Con ValueFrom, la celda muestra Values[valor de la columna ValueFrom] de la fila.
/// En una columna "select" las opciones son las claves de TableRow.Values.
/// </summary>
public sealed record TableColumn(string Key, string Label, string Type = "text", string? ValueFrom = null);

public sealed record TableRow(
    IReadOnlyDictionary<string, object?> Cells,
    IReadOnlyList<string> ElementIds,
    string? Id = null,
    IReadOnlyDictionary<string, double>? Values = null);

public sealed record TableResult(
    string Title,
    IReadOnlyList<TableColumn> Columns,
    IReadOnlyList<TableRow> Rows,
    string? Summary = null,
    string? GroupBy = null) : PluginResult;

public sealed record FileResult(string FileName, string ContentType, byte[] Content) : PluginResult;

public sealed record ErrorResult(string Message) : PluginResult;
