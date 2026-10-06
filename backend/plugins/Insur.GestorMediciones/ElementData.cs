using Xbim.Common;
using Xbim.Ifc4.Interfaces;

namespace Insur.GestorMediciones;

internal sealed class ModelIndex
{
    public Dictionary<int, List<IIfcPropertySetDefinition>> Definitions { get; } = new();
    public Dictionary<int, IIfcTypeObject> Types { get; } = new();
    public Dictionary<int, List<IIfcMaterialSelect>> Materials { get; } = new();
    public Dictionary<int, TypeData> TypeCache { get; } = new();
    public HashSet<int> Voided { get; } = new();

    public static ModelIndex Build(IModel model)
    {
        var index = new ModelIndex();

        foreach (var rel in model.Instances.OfType<IIfcRelVoidsElement>())
            if (rel.RelatingBuildingElement is not null)
                index.Voided.Add(rel.RelatingBuildingElement.EntityLabel);

        foreach (var rel in model.Instances.OfType<IIfcRelDefinesByProperties>())
        {
            if (rel.RelatingPropertyDefinition is not IIfcPropertySetDefinition def) continue;
            foreach (var obj in rel.RelatedObjects)
                Add(index.Definitions, obj.EntityLabel, def);
        }

        foreach (var rel in model.Instances.OfType<IIfcRelDefinesByType>())
        {
            if (rel.RelatingType is null) continue;
            foreach (var obj in rel.RelatedObjects)
                index.Types[obj.EntityLabel] = rel.RelatingType;
        }

        foreach (var rel in model.Instances.OfType<IIfcRelAssociatesMaterial>())
        {
            if (rel.RelatingMaterial is null) continue;
            foreach (var obj in rel.RelatedObjects)
                if (obj is IPersistEntity entity)
                    Add(index.Materials, entity.EntityLabel, rel.RelatingMaterial);
        }

        return index;
    }

    private static void Add<T>(Dictionary<int, List<T>> map, int label, T value)
    {
        if (!map.TryGetValue(label, out var list)) map[label] = list = [];
        list.Add(value);
    }
}

internal sealed class TypeData
{
    public Dictionary<string, string> Props { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, double> Quantities { get; } = new(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyList<IIfcPropertySetDefinition> Definitions { get; init; } = [];
}

internal sealed class ElementData
{
    private readonly Dictionary<string, string> _props = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, double> _quantities = new(StringComparer.OrdinalIgnoreCase);
    private readonly IIfcElement _element;
    private readonly IIfcTypeObject? _type;
    private readonly ModelIndex _index;
    private readonly TypeData? _typeData;
    private readonly List<IIfcPropertySetDefinition> _ownDefinitions;
    private readonly double _lengthFactor;
    private bool _quantitiesRead;

    public string TypeName { get; }
    public string Family { get; }
    public string TypeShortName { get; }

    private ElementData(IIfcElement element, IIfcTypeObject? type, TypeData? typeData,
        List<IIfcPropertySetDefinition> ownDefinitions, ModelIndex index, double lengthFactor)
    {
        _element = element;
        _type = type;
        _typeData = typeData;
        _ownDefinitions = ownDefinitions;
        _index = index;
        _lengthFactor = lengthFactor;

        var full = type?.Name?.ToString() ?? element.ObjectType?.ToString() ?? element.Name?.ToString() ?? "";
        TypeName = full;
        var parts = full.Split(':');
        Family = parts.Length > 1 ? parts[0].Trim() : "";
        TypeShortName = parts.Length > 1 ? parts[1].Trim() : full.Trim();
    }

    public static ElementData Read(IIfcElement element, double lengthFactor, ModelIndex index)
    {
        index.Types.TryGetValue(element.EntityLabel, out var type);

        TypeData? typeData = null;
        if (type is not null && !index.TypeCache.TryGetValue(type.EntityLabel, out typeData))
        {
            typeData = new TypeData { Definitions = type.HasPropertySets?.ToList() ?? [] };
            foreach (var def in typeData.Definitions) ReadProps(def, typeData.Props);
            index.TypeCache[type.EntityLabel] = typeData;
        }

        index.Definitions.TryGetValue(element.EntityLabel, out var own);
        var data = new ElementData(element, type, typeData, own ?? [], index, lengthFactor);
        foreach (var def in data._ownDefinitions) ReadProps(def, data._props);
        return data;
    }

    private static void ReadProps(IIfcPropertySetDefinition def, Dictionary<string, string> target)
    {
        if (def is not IIfcPropertySet pset) return;
        foreach (var prop in pset.HasProperties.OfType<IIfcPropertySingleValue>())
        {
            var value = prop.NominalValue?.Value?.ToString();
            if (!string.IsNullOrWhiteSpace(value)) target[prop.Name.ToString()] = value;
        }
    }

    private static void ReadQuantities(IIfcPropertySetDefinition def, double f, Dictionary<string, double> target)
    {
        if (def is not IIfcElementQuantity eq) return;
        foreach (var q in eq.Quantities)
        {
            var name = q.Name.ToString();
            switch (q)
            {
                case IIfcQuantityLength l: target[name] = (double)l.LengthValue * f; break;
                case IIfcQuantityArea a: target[name] = (double)a.AreaValue * f * f; break;
                case IIfcQuantityVolume v: target[name] = (double)v.VolumeValue * f * f * f; break;
                case IIfcQuantityCount c: target[name] = (double)c.CountValue; break;
            }
        }
    }

    private void EnsureQuantities()
    {
        if (_quantitiesRead) return;
        _quantitiesRead = true;

        if (_typeData is not null)
        {
            if (_typeData.Quantities.Count == 0)
                foreach (var def in _typeData.Definitions)
                    ReadQuantities(def, _lengthFactor, _typeData.Quantities);
            foreach (var (k, v) in _typeData.Quantities) _quantities[k] = v;
        }

        foreach (var def in _ownDefinitions) ReadQuantities(def, _lengthFactor, _quantities);
    }

    public string? Prop(string name)
    {
        if (_props.TryGetValue(name, out var v)) return v.Trim();
        return _typeData is not null && _typeData.Props.TryGetValue(name, out var tv) ? tv.Trim() : null;
    }

    public double? Qty(params string[] names)
    {
        EnsureQuantities();
        foreach (var n in names)
            if (_quantities.TryGetValue(n, out var v) && v > 0) return v;
        return null;
    }

    public IEnumerable<string> MaterialCodes(string prefix)
    {
        var codes = new List<string>();
        foreach (var m in Materials(_element.EntityLabel).Concat(_type is null ? [] : Materials(_type.EntityLabel)))
        {
            var name = m.Name.ToString();
            if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            var code = name[prefix.Length..].Trim();
            if (code.Length > 0 && !codes.Contains(code)) codes.Add(code);
        }
        return codes;
    }

    private IEnumerable<IIfcMaterial> Materials(int label)
    {
        if (!_index.Materials.TryGetValue(label, out var selects)) yield break;
        foreach (var select in selects)
        {
            switch (select)
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
