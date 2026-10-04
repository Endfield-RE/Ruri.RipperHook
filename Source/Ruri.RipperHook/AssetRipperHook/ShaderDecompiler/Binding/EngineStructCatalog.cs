using AssetRipper.Import.Logging;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Model.CustomAttributes;
using Ruri.ShaderTools;

namespace Ruri.RipperHook.AR;

/// <summary>
/// Constant-buffer layouts as the engine's managed code states them: a value type named like the buffer and uploaded as its
/// raw bytes, so each instance field is a member at its own offset. A field's shape follows from its type alone: a four-byte
/// primitive is a scalar, a value type of one to four such fields laid end to end is a vector of that many components, the
/// engine's 4x4 matrix is a matrix, and a fixed buffer of N four-byte elements is N/4 four-component registers -- the only
/// way such bytes read as constant-buffer members. A same-named type with a field of no such shape is reported and dropped.
/// </summary>
public static class EngineStructCatalog
{
    private const string FixedBufferAttribute = "System.Runtime.CompilerServices.FixedBufferAttribute";
    private const string Matrix4x4 = "UnityEngine.Matrix4x4";

    private readonly record struct Shape(ShaderParamType Type, int Rows, int Columns, int Bytes);

    public static ConstantBufferCatalog FromIl2Cpp(ApplicationAnalysisContext app)
    {
        ILookup<string, TypeAnalysisContext> valueTypes = app.AllTypes
            .Where(static type => type.IsValueType)
            .ToLookup(static type => type.Name, StringComparer.Ordinal);
        return new ConstantBufferCatalog(name => LayoutsNamed(name, valueTypes[name]));
    }

    private static IReadOnlyList<ConstantBufferCatalog.Layout> LayoutsNamed(string name, IEnumerable<TypeAnalysisContext> types)
    {
        var layouts = new List<ConstantBufferCatalog.Layout>();
        foreach (TypeAnalysisContext type in types)
        {
            if (LayoutOf(type, out string? rejection) is { } layout)
            {
                layouts.Add(layout);
            }
            else
            {
                Logger.Warning(LogCategory.Export, $"value type {type.FullName} is named like constant buffer {name} but {rejection}");
            }
        }
        return layouts;
    }

    private static ConstantBufferCatalog.Layout? LayoutOf(TypeAnalysisContext type, out string? rejection)
    {
        var members = new List<ConstantBufferCatalog.Member>();
        int end = 0;
        foreach (FieldAnalysisContext field in type.Fields)
        {
            if (field.IsStatic)
            {
                continue;
            }
            if (MemberOf(field, out rejection) is not { } member)
            {
                return null;
            }
            members.Add(member);
            end = Math.Max(end, member.Offset + member.ByteSize);
        }

        rejection = members.Count == 0 ? "holds no instance field" : null;
        return members.Count == 0 ? null : new ConstantBufferCatalog.Layout(type.FullName, end, members);
    }

    private static ConstantBufferCatalog.Member? MemberOf(FieldAnalysisContext field, out string? rejection)
    {
        rejection = null;
        field.AnalyzeCustomAttributeData(false);
        AnalyzedCustomAttribute? fixedBuffer = field.CustomAttributes?
            .FirstOrDefault(static attribute => attribute.Constructor.DeclaringType?.FullName == FixedBufferAttribute);
        if (fixedBuffer is null)
        {
            if (ShapeOf(field.FieldType) is { } shape)
            {
                return new ConstantBufferCatalog.Member(field.Name, field.Offset, shape.Bytes, shape.Type, shape.Rows, shape.Columns, 0);
            }
            rejection = $"field {field.Name} of type {field.FieldType.FullName} has no constant-buffer shape";
            return null;
        }

        if (fixedBuffer is not { ConstructorParameters.Count: 2 }
            || fixedBuffer.ConstructorParameters[0] is not CustomAttributeTypeParameter { TypeContext: { } element }
            || fixedBuffer.ConstructorParameters[1] is not CustomAttributePrimitiveParameter { PrimitiveValue: { } length }
            || ScalarOf(element.FullName) is not { } scalar)
        {
            rejection = $"field {field.Name} of type {field.FieldType.FullName} has no constant-buffer shape";
            return null;
        }

        int count = Convert.ToInt32(length);
        if (count % 4 != 0)
        {
            rejection = $"fixed buffer {field.Name} holds {count} elements, not whole registers";
            return null;
        }
        return new ConstantBufferCatalog.Member(field.Name, field.Offset, count * 4, scalar.Type, 1, 4, count / 4);
    }

    private static Shape? ShapeOf(TypeAnalysisContext type)
    {
        if (ScalarOf(type.FullName) is { } scalar)
        {
            return scalar;
        }
        if (type.FullName == Matrix4x4)
        {
            return new Shape(ShaderParamType.Float, 4, 4, 64);
        }
        if (!type.IsValueType)
        {
            return null;
        }

        List<FieldAnalysisContext> components = type.Fields.Where(static field => !field.IsStatic).ToList();
        if (components.Count is < 1 or > 4 || ScalarOf(components[0].FieldType.FullName) is not { } component)
        {
            return null;
        }
        for (int index = 0; index < components.Count; index++)
        {
            if (components[index].Offset != index * 4 || components[index].FieldType.FullName != components[0].FieldType.FullName)
            {
                return null;
            }
        }
        return new Shape(component.Type, 1, components.Count, components.Count * 4);
    }

    private static Shape? ScalarOf(string fullName) => fullName switch
    {
        "System.Single" => new Shape(ShaderParamType.Float, 1, 1, 4),
        "System.Int32" => new Shape(ShaderParamType.Int, 1, 1, 4),
        "System.UInt32" => new Shape(ShaderParamType.UInt, 1, 1, 4),
        _ => null,
    };
}
