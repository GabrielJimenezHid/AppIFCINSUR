using ClosedXML.Excel;
using Insur.Plugins.Abstractions;
using Xbim.Ifc;

namespace Insur.GestorMediciones;

public sealed class GestorMedicionesPlugin : IIfcPlugin
{
    public PluginManifest Manifest { get; } = new(
        Id: "gestor-mediciones",
        Name: "Gestor de Mediciones",
        Version: "0.1.0",
        Description: "Agrupa los elementos del IFC por categoría, tipo y código de medición y suma sus cantidades.",
        Parameters:
        [
            new PluginParameter("excelCodigos", "Excel de códigos (IDS-CodigosPrestoBlanco.xlsx)", "file"),
            new PluginParameter("incluirNoMedible", "Incluir elementos 'No Medible'", "bool", "false"),
            new PluginParameter("eliminarDuplicados", "Descartar elementos duplicados", "bool", "true"),
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
        var options = new MedicionOptions(
            IncluirNoMedible: IsTrue(context, "incluirNoMedible", false),
            EliminarDuplicados: IsTrue(context, "eliminarDuplicados", true));

        var codes = context.Files.TryGetValue("excelCodigos", out var excelPath)
            ? CodeDictionary.Load(excelPath)
            : new Dictionary<string, string>();

        var sw = System.Diagnostics.Stopwatch.StartNew();
        using var model = IfcStore.Open(context.IfcPath);
        var openMs = sw.ElapsedMilliseconds;
        sw.Restart();
        var rows = MedicionExtractor.Extract(model, options, codes);
        var extractMs = sw.ElapsedMilliseconds;
        Console.WriteLine($"[GestorMediciones] apertura IFC: {openMs} ms, extraccion: {extractMs} ms, filas: {rows.Count}");

        var groups = rows
            .GroupBy(r => (r.Categoria, r.Familia, r.Tipo, r.Criterio, r.Codigo))
            .Select(g => new MedicionGroup(
                g.Key.Categoria, g.Key.Familia, g.Key.Tipo, g.Key.Criterio, g.Key.Codigo,
                g.First().Descripcion, g.First().Capitulo,
                g.Select(r => r.GlobalId).Distinct().ToList(),
                g.Key.Criterio == "ud" ? g.Select(r => r.GlobalId).Distinct().Count() : g.Sum(r => r.Cantidad)))
            .OrderBy(g => g.Capitulo, StringComparer.Ordinal).ThenBy(g => g.Codigo).ThenBy(g => g.Categoria)
            .ThenBy(g => g.Familia).ThenBy(g => g.Tipo)
            .ToList();

        return context.Action == "export"
            ? new FileResult("Mediciones.xlsx",
                "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", ToExcel(groups))
            : ToTable(groups);
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
            new("criterio", "Criterio"),
            new("elementos", "Nº elem.", "number"),
            new("cantidad", "Medición", "number"),
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
                ["criterio"] = Etiqueta(g.Criterio),
                ["elementos"] = g.GlobalIds.Count,
                ["cantidad"] = g.Criterio == "ud"
                    ? g.Cantidad.ToString("N0", Es)
                    : g.Cantidad.ToString("N2", Es),
            },
            g.GlobalIds)).ToList();

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
                ws.Cell(r, 1).Value = g.Categoria;
                ws.Cell(r, 2).Value = string.Join(" | ", new[] { g.Familia, g.Tipo }.Where(s => s.Length > 0));
                ws.Cell(r, 3).Value = g.Codigo;
                ws.Cell(r, 4).Value = g.Descripcion;
                ws.Cell(r, 5).Value = Etiqueta(g.Criterio);
                ws.Cell(r, 6).Value = g.GlobalIds.Count;
                ws.Cell(r, 7).Value = g.Criterio == "ud" ? Math.Round(g.Cantidad) : Math.Round(g.Cantidad, 2);
                ws.Cell(r, 7).Style.NumberFormat.Format = g.Criterio == "ud" ? "#,##0" : "#,##0.00";
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

    private static readonly System.Globalization.CultureInfo Es = new("es-ES");

    private static string Etiqueta(string criterio) => criterio == "m2cc" ? "m2CC" : criterio;

    private static bool IsTrue(PluginContext ctx, string name, bool fallback) =>
        ctx.Parameters.TryGetValue(name, out var v) && bool.TryParse(v, out var b) ? b : fallback;
}

internal sealed record MedicionOptions(bool IncluirNoMedible, bool EliminarDuplicados);

internal sealed record MedicionRow(
    string Categoria, string Familia, string Tipo, string Criterio, string Codigo,
    string Descripcion, string Capitulo, string GlobalId,
    double Cantidad);

internal sealed record MedicionGroup(
    string Categoria, string Familia, string Tipo, string Criterio, string Codigo,
    string Descripcion, string Capitulo, List<string> GlobalIds,
    double Cantidad);

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
