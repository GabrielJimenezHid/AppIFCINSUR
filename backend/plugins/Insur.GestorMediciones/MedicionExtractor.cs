using System.Globalization;
using Xbim.Ifc;
using Xbim.Ifc4.Interfaces;

namespace Insur.GestorMediciones;

internal enum Tipo { Otro, Muro, Suelo, Cubierta, Techo, Barandilla, Pilar, Armazon }

internal static class MedicionExtractor
{
    private const string ParamCodigo = "IDS-MED-CodigoMedicion";
    private const string ParamCriterio = "IDS-MED-CriterioMedicion";
    private const string PrefijoMaterial = "IDS-M-";
    private const string NoMedible = "No Medible";

    private static readonly string[] ParamsVentanaAnidados =
    [
        "IDS-MED-CodigoMedicionAnidado-Vidrio",
        "IDS-MED-CodigoMedicionAnidado-TratamientoVidrio",
        "IDS-MED-CodigoMedicionAnidado-Alfeizar",
        "IDS-MED-CodigoMedicionAnidado-Dintel",
        "IDS-MED-CodigoMedicionAnidado-Persiana",
        "IDS-MED-CodigoMedicionAnidado-MecanismoOcilobatiente",
    ];

    public static List<MedicionRow> Extract(IfcStore model, MedicionOptions options, Dictionary<string, string> codes)
    {
        var f = model.ModelFactors.LengthToMetresConversionFactor;
        var rows = new List<MedicionRow>();
        var seen = new HashSet<string>();

        var index = ModelIndex.Build(model);

        foreach (var elem in model.Instances.OfType<IIfcElement>())
        {
            var (categoria, tipo) = Categorize(elem);
            if (categoria is null) continue;

            var data = ElementData.Read(elem, f, index);
            var codigo = data.Prop(ParamCodigo);
            if (string.IsNullOrWhiteSpace(codigo)) continue;
            if (!options.IncluirNoMedible && codigo.Equals(NoMedible, StringComparison.OrdinalIgnoreCase)) continue;

            var criterio = data.Prop(ParamCriterio);
            if (tipo == Tipo.Techo) criterio = "m2";
            if (string.IsNullOrWhiteSpace(criterio)) criterio = CriterioPorDefecto(tipo);
            criterio = criterio.Trim().ToLowerInvariant();

            var ml = data.Qty("Length") ?? 0;
            var m2 = tipo switch
            {
                Tipo.Muro => data.Qty("NetSideArea", "GrossSideArea", "Area", "NetArea", "GrossArea"),
                _ => data.Qty("NetArea", "Area", "GrossArea", "NetSideArea", "GrossFootprintArea"),
            } ?? 0;
            var m3 = data.Qty("NetVolume", "GrossVolume") ?? 0;
            var cantidad = Cantidad(tipo, criterio, data, ml, m2, m3);

            if (options.EliminarDuplicados)
            {
                var key = $"{categoria}|{data.TypeName}|{codigo}|{Round(ml)}|{Round(m2)}|{Round(m3)}|{PlacementKey(elem)}";
                if (!seen.Add(key)) continue;
            }

            var familia = data.Family;
            var nombreTipo = data.TypeShortName;
            var guid = elem.GlobalId.ToString();

            string Desc(string c) => codes.TryGetValue(c, out var d) ? d : "";

            rows.Add(new MedicionRow(categoria, familia, nombreTipo, criterio, codigo, Desc(codigo),
                Capitulo(codigo), guid, cantidad, ml, m2, m3));

            if (tipo is Tipo.Muro or Tipo.Suelo or Tipo.Techo)
            {
                foreach (var codMat in data.MaterialCodes(PrefijoMaterial))
                {
                    var desc = codes.TryGetValue(codMat, out var d)
                        ? d
                        : string.Join(" | ", new[] { familia, nombreTipo }.Where(s => !string.IsNullOrWhiteSpace(s)));
                    rows.Add(new MedicionRow(categoria + "-Material", familia, nombreTipo, "m2", codMat, desc,
                        Capitulo(codMat), guid, m2, ml, m2, m3));
                }
            }

            if (categoria == "Ventanas")
            {
                foreach (var param in ParamsVentanaAnidados)
                {
                    var anidado = data.Prop(param);
                    if (string.IsNullOrWhiteSpace(anidado)) continue;
                    rows.Add(new MedicionRow(categoria, familia, nombreTipo, criterio, anidado, Desc(anidado),
                        Capitulo(anidado), guid, cantidad, ml, m2, m3));
                }
            }
        }

        return rows;
    }

    private static double Cantidad(Tipo tipo, string criterio, ElementData data, double ml, double m2, double m3)
    {
        if (criterio == "ud") return 1;
        switch (tipo)
        {
            case Tipo.Muro:
                if (criterio == "ml") return ml;
                if (criterio == "m3") return m3;
                if (criterio == "m2cc") return ml * (data.Qty("Height") ?? 0);
                return m2;
            case Tipo.Suelo:
                return criterio == "m3" ? m3 : m2;
            case Tipo.Cubierta:
            case Tipo.Techo:
                return m2;
            case Tipo.Barandilla:
                return ml;
            case Tipo.Pilar:
                return criterio == "m3" ? m3 : 1;
            case Tipo.Armazon:
                return criterio == "m3" ? m3 : ml;
            default:
                return 1;
        }
    }

    private static string CriterioPorDefecto(Tipo tipo) => tipo switch
    {
        Tipo.Muro or Tipo.Suelo or Tipo.Techo or Tipo.Cubierta => "m2",
        Tipo.Barandilla or Tipo.Armazon => "ml",
        _ => "ud",
    };

    private static (string? Categoria, Tipo Tipo) Categorize(IIfcElement elem) => elem switch
    {
        IIfcWall => ("Muros", Tipo.Muro),
        IIfcSlab s when s.PredefinedType == IfcSlabTypeEnum.ROOF => ("Cubiertas", Tipo.Cubierta),
        IIfcSlab => ("Suelos", Tipo.Suelo),
        IIfcRoof => ("Cubiertas", Tipo.Cubierta),
        IIfcCovering c when c.PredefinedType == IfcCoveringTypeEnum.CEILING => ("Techos", Tipo.Techo),
        IIfcRailing => ("Barandillas", Tipo.Barandilla),
        IIfcDoor => ("Puertas", Tipo.Otro),
        IIfcWindow => ("Ventanas", Tipo.Otro),
        IIfcColumn => ("Pilares estructurales", Tipo.Pilar),
        IIfcBeam or IIfcMember => ("Armazón estructural", Tipo.Armazon),
        IIfcFurnishingElement => ("Mobiliario", Tipo.Otro),
        IIfcBuildingElementProxy => ("Modelos genéricos", Tipo.Otro),
        IIfcStair or IIfcStairFlight => ("Escaleras", Tipo.Otro),
        IIfcOpeningElement or IIfcVirtualElement => (null, Tipo.Otro),
        _ => ("Otros", Tipo.Otro),
    };

    private static readonly Dictionary<string, string> MapaPrefijo = new()
    {
        ["03"] = "C03-CIMENTACIONES",
        ["04"] = "C04-ESTRUCTURAS",
        ["05"] = "C05-CUBIERTAS",
        ["06"] = "C06-CERRAMIENTOS, DIVISIONES Y ALBAÑILERIA",
        ["07"] = "C07-AISLAMIENTOS E IMPERMEABILIZACIONES",
        ["08"] = "C08-REVESTIMIENTOS",
        ["09"] = "C09-CARPINTERIA EXTERIOR",
        ["10"] = "C10-CARPINTERIA DE MADERA",
        ["11"] = "C11-CARPINTERIA METALICA-CERRAJERIA",
        ["12"] = "C12-VIDRIOS",
        ["13"] = "C13-PINTURAS",
        ["14"] = "C14-URBANIZACION INTERIOR",
        ["15"] = "C15-URBANIZACION EXTERIOR",
        ["16"] = "C16-DECORACION Y VARIOS",
        ["17"] = "C17-SISTEMAS INDUSTRIALIZADOS",
        ["31"] = "C31-INSTALACIÓN DE FONTANERÍA",
    };

    public static string Capitulo(string codigo)
    {
        if (codigo.Equals(NoMedible, StringComparison.OrdinalIgnoreCase)) return "NO MEDIBLE";
        var limpio = codigo.TrimStart('C', 'c').Replace(".", "").Trim();
        return limpio.Length >= 2 && MapaPrefijo.TryGetValue(limpio[..2], out var cap)
            ? cap
            : "SIN CLASIFICAR";
    }

    private static string Round(double v) => Math.Round(v, 3).ToString(CultureInfo.InvariantCulture);

    private static string PlacementKey(IIfcElement elem)
    {
        var parts = new List<string>();
        var placement = elem.ObjectPlacement as IIfcLocalPlacement;
        var guard = 0;
        while (placement is not null && guard++ < 32)
        {
            if (placement.RelativePlacement is IIfcAxis2Placement3D axis)
            {
                var c = axis.Location.Coordinates;
                parts.Add(string.Join(",", c.Select(v => Round(v))));
            }
            placement = placement.PlacementRelTo as IIfcLocalPlacement;
        }
        return string.Join("|", parts);
    }
}
