using System.Globalization;
using System.Text.Json;
using ClosedXML.Excel;
using Insur.Plugins.Abstractions;
using Xbim.Ifc;

namespace Insur.GestorMediciones;

public sealed class GestorMedicionesPlugin : IIfcPlugin
{
    public PluginManifest Manifest { get; } = new(
        Id: "gestor-mediciones",
        Name: "Gestor de Mediciones",
        Version: "0.2.0",
        Description: "Agrupa los elementos del IFC por capítulo, tipo y código de medición y suma sus cantidades según el criterio.",
        Parameters:
        [
            new PluginParameter("excelCodigos", "Excel de códigos (IDS-CodigosPrestoBlanco.xlsx)", "file"),
            new PluginParameter("incluirNoMedible", "Incluir elementos 'No Medible'", "bool", "false"),
        ],
        Actions:
        [
            new PluginAction("run", "Calcular mediciones"),
            new PluginAction("export", "Exportar a Excel"),
        ]);

    public Task<PluginResult> RunAsync(PluginContext context, CancellationToken cancellationToken) =>
        Task.Run(() => Run(context), cancellationToken);

    private PluginResult Run(PluginContext context)
    {
        var options = new MedicionOptions(IncluirNoMedible: IsTrue(context, "incluirNoMedible", false));

        var codes = context.Files.TryGetValue("excelCodigos", out var excelPath)
            ? CodeDictionary.Load(excelPath)
            : new Dictionary<string, string>();

        using var model = IfcStore.Open(context.IfcPath);
        var rows = MedicionExtractor.Extract(model, options, codes);

        var overrides = ReadOverrides(context);
        var groups = rows
            .GroupBy(r => (r.Categoria, r.Familia, r.Tipo, r.Criterio, r.Codigo))
            .Select(g => BuildGroup(g.Key, g.ToList(), overrides))
            .OrderBy(g => g.Capitulo, StringComparer.Ordinal).ThenBy(g => g.Codigo).ThenBy(g => g.Categoria)
            .ThenBy(g => g.Familia).ThenBy(g => g.Tipo)
            .ToList();

        return context.Action == "export"
            ? new FileResult("Mediciones.xlsx",
                "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", ToExcel(groups))
            : ToTable(groups);
    }

    private static MedicionGroup BuildGroup(
        (string Categoria, string Familia, string Tipo, string Criterio, string Codigo) key,
        List<MedicionRow> rows, Dictionary<string, string> overrides)
    {
        var first = rows[0];
        var guids = rows.Select(r => r.GlobalId).Distinct().ToList();

        var values = new Dictionary<string, double>();
        foreach (var option in first.Permitidos)
        {
            values[Etiqueta(option)] = option switch
            {
                "ml" => rows.Sum(r => r.Medidas.Ml),
                "m2" => rows.Sum(r => r.Medidas.M2),
                "m3" => rows.Sum(r => r.Medidas.M3),
                "m2cc" => rows.Sum(r => r.Medidas.Cc),
                _ => guids.Count,
            };
        }

        var id = string.Join("|", key.Categoria, key.Familia, key.Tipo, key.Criterio, key.Codigo);
        var criterio = Etiqueta(key.Criterio);
        if (overrides.TryGetValue(id, out var chosen) && values.ContainsKey(Etiqueta(chosen.ToLowerInvariant())))
            criterio = Etiqueta(chosen.ToLowerInvariant());

        return new MedicionGroup(id, key.Categoria, key.Familia, key.Tipo, criterio, key.Codigo,
            first.Descripcion, first.Capitulo, guids, values);
    }

    private static Dictionary<string, string> ReadOverrides(PluginContext context)
    {
        if (!context.Parameters.TryGetValue("overrides", out var json) || string.IsNullOrWhiteSpace(json))
            return new Dictionary<string, string>();
        try { return JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? new(); }
        catch (JsonException) { return new Dictionary<string, string>(); }
    }

    private static TableResult ToTable(List<MedicionGroup> groups)
    {
        TableColumn[] columns =
        [
            new("categoria", "Categoría"),
            new("familia", "Familia"),
            new("tipo", "Tipo"),
            new("codigo", "Código"),
            new("capitulo", "Capítulo"),
            new("descripcion", "Descripción"),
            new("criterio", "Criterio", "select"),
            new("elementos", "Nº elem.", "number"),
            new("cantidad", "Medición", "number", ValueFrom: "criterio"),
        ];

        var rows = groups.Select(g => new TableRow(
            new Dictionary<string, object?>
            {
                ["categoria"] = g.Categoria,
                ["familia"] = g.Familia,
                ["tipo"] = g.Tipo,
                ["codigo"] = g.Codigo,
                ["capitulo"] = g.Capitulo,
                ["descripcion"] = g.Descripcion,
                ["criterio"] = g.Criterio,
                ["elementos"] = g.GlobalIds.Count,
            },
            g.GlobalIds,
            g.Id,
            g.Values)).ToList();

        return new TableResult("Gestor de Mediciones", columns, rows,
            $"{groups.Count} partidas, {groups.Select(g => g.Capitulo).Distinct().Count()} capítulos",
            GroupBy: "capitulo");
    }

    private static byte[] ToExcel(List<MedicionGroup> groups)
    {
        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add("Mediciones");
        string[] headers =
            ["Categoría", "Familia y Tipo", "Código", "Descripción", "Criterio",
             "Nº elementos", "Medición"];
        const int lastCol = 7;

        var r = 1;
        foreach (var chapter in groups.GroupBy(g => g.Capitulo))
        {
            var title = ws.Range(r, 1, r, lastCol);
            title.Merge();
            ws.Cell(r, 1).Value = chapter.Key;
            title.Style.Fill.BackgroundColor = XLColor.FromHtml("#005288");
            title.Style.Font.FontColor = XLColor.White;
            title.Style.Font.Bold = true;
            r++;

            for (var c = 0; c < headers.Length; c++) ws.Cell(r, c + 1).Value = headers[c];
            var head = ws.Range(r, 1, r, lastCol);
            head.Style.Font.Bold = true;
            head.Style.Fill.BackgroundColor = XLColor.LightGray;
            r++;

            foreach (var g in chapter)
            {
                var valor = g.Values[g.Criterio];
                var esUd = g.Criterio == "ud";
                ws.Cell(r, 1).Value = g.Categoria;
                ws.Cell(r, 2).Value = string.Join(" | ", new[] { g.Familia, g.Tipo }.Where(s => s.Length > 0));
                ws.Cell(r, 3).Value = g.Codigo;
                ws.Cell(r, 4).Value = g.Descripcion;
                ws.Cell(r, 5).Value = g.Criterio;
                ws.Cell(r, 6).Value = g.GlobalIds.Count;
                ws.Cell(r, 7).Value = esUd ? Math.Round(valor) : Math.Round(valor, 2);
                ws.Cell(r, 7).Style.NumberFormat.Format = esUd ? "#,##0" : "#,##0.00";
                r++;
            }
            r++;
        }

        ws.Columns().AdjustToContents();
        ws.Column(2).Width = 45;
        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }

    private static string Etiqueta(string criterio) => criterio == "m2cc" ? "m2CC" : criterio;

    private static bool IsTrue(PluginContext ctx, string name, bool fallback) =>
        ctx.Parameters.TryGetValue(name, out var v) && bool.TryParse(v, out var b) ? b : fallback;
}

internal sealed record MedicionOptions(bool IncluirNoMedible);

internal sealed record MedicionRow(
    string Categoria, string Familia, string Tipo, string Criterio, string Codigo,
    string Descripcion, string Capitulo, string GlobalId,
    Medidas Medidas, string[] Permitidos);

internal sealed record MedicionGroup(
    string Id, string Categoria, string Familia, string Tipo, string Criterio, string Codigo,
    string Descripcion, string Capitulo, List<string> GlobalIds,
    Dictionary<string, double> Values);

internal static class CodeDictionary
{
    public static Dictionary<string, string> Load(string path)
    {
        var dict = new Dictionary<string, string>();
        using var wb = new XLWorkbook(path);
        var ws = wb.Worksheet("CapitulosYCodigos");
        foreach (var row in ws.RowsUsed().Skip(1))
        {
            var key = row.Cell(3).GetString().Trim();
            if (key.Length > 0) dict[key] = row.Cell(6).GetString().Trim();
        }
        return dict;
    }
}
