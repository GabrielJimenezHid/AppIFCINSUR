using Xbim.Ifc4.Interfaces;

namespace Insur.GestorMediciones;

internal sealed class ElementData
{
    private readonly Dictionary<string, string> _props = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, double> _quantities = new(StringComparer.OrdinalIgnoreCase);
    private readonly IIfcElement _element;
    private readonly IIfcTypeObject? _type;

    public string TypeName { get; }
    public string Family { get; }
    public string TypeShortName { get; }

    private ElementData(IIfcElement element, IIfcTypeObject? type)
    {
        _element = element;
        _type = type;

        var full = type?.Name?.ToString() ?? element.ObjectType?.ToString() ?? element.Name?.ToString() ?? "";
        TypeName = full;
        var parts = full.Split(':');
        Family = parts.Length > 1 ? parts[0].Trim() : "";
        TypeShortName = parts.Length > 1 ? parts[1].Trim() : full.Trim();
    }

    public static ElementData Read(IIfcElement element, double lengthFactor)
    {
        var type = element.IsTypedBy.FirstOrDefault()?.RelatingType;
        var data = new ElementData(element, type);

        if (type?.HasPropertySets is not null)
            foreach (var def in type.HasPropertySets) data.ReadDefinition(def, lengthFactor);

        foreach (var rel in element.IsDefinedBy)
            if (rel.RelatingPropertyDefinition is IIfcPropertySetDefinition def)
                data.ReadDefinition(def, lengthFactor);

        return data;
    }

    private void ReadDefinition(IIfcPropertySetDefinition def, double f)
    {
        switch (def)
        {
            case IIfcPropertySet pset:
                foreach (var prop in pset.HasProperties.OfType<IIfcPropertySingleValue>())
                {
                    var value = prop.NominalValue?.Value?.ToString();
                    if (!string.IsNullOrWhiteSpace(value)) _props[prop.Name.ToString()] = value;
                }
                break;

            case IIfcElementQuantity eq:
                foreach (var q in eq.Quantities)
                {
                    var name = q.Name.ToString();
                    switch (q)
                    {
                        case IIfcQuantityLength l: _quantities[name] = (double)l.LengthValue * f; break;
                        case IIfcQuantityArea a: _quantities[name] = (double)a.AreaValue * f * f; break;
                        case IIfcQuantityVolume v: _quantities[name] = (double)v.VolumeValue * f * f * f; break;
                        case IIfcQuantityCount c: _quantities[name] = (double)c.CountValue; break;
                    }
                }
                break;
        }
    }

    public string? Prop(string name) => _props.GetValueOrDefault(name)?.Trim();

    public double? Qty(params string[] names)
    {
        foreach (var n in names)
            if (_quantities.TryGetValue(n, out var v) && v > 0) return v;
        return null;
    }

    public IEnumerable<string> MaterialCodes(string prefix)
    {
        var codes = new List<string>();
        foreach (var m in Materials(_element).Concat(_type is null ? [] : Materials(_type)))
        {
            var name = m.Name.ToString();
            if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            var code = name[prefix.Length..].Trim();
            if (code.Length > 0 && !codes.Contains(code)) codes.Add(code);
        }
        return codes;
    }

    private static IEnumerable<IIfcMaterial> Materials(IIfcObjectDefinition obj)
    {
        foreach (var rel in obj.HasAssociations.OfType<IIfcRelAssociatesMaterial>())
        {
            switch (rel.RelatingMaterial)
            {
                case IIfcMaterial m:
                    yield return m;
                    break;
                case IIfcMaterialLayerSetUsage u:
                    foreach (var l in u.ForLayerSet.MaterialLayers)
                        if (l.Material is not null) yield return l.Material;
                    break;
                case IIfcMaterialLayerSet s:
                    foreach (var l in s.MaterialLayers)
                        if (l.Material is not null) yield return l.Material;
                    break;
                case IIfcMaterialLayer l:
                    if (l.Material is not null) yield return l.Material;
                    break;
                case IIfcMaterialList list:
                    foreach (var m in list.Materials) yield return m;
                    break;
            }
        }
    }
}
