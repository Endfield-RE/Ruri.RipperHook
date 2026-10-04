using Ruri.ShaderTools;
using Ruri.ShaderTools.Binding;
using Ruri.ShaderTools.Spirv;

namespace Ruri.RipperHook.AR;

/// <summary>
/// Names the fields of a struct held by a constant-buffer member from the parameters' struct table: the buffer found by its
/// own descriptor set and binding, each field at the struct's offset plus the field's offset. For compilers that keep typed
/// blocks and strip member names, where the table names the struct and nothing names its fields.
/// </summary>
public sealed class NestedStructMemberBinder : IModuleSymbolBinder
{
    public static NestedStructMemberBinder Instance { get; } = new();

    private NestedStructMemberBinder()
    {
    }

    public void Bind(ModuleLayout module, SerializedProgramData symbols, ICollection<BoundMemberName> names)
    {
        foreach (ModuleBlock block in module.Blocks)
        {
            if (block.StorageClass != StorageClass.Uniform
                || ConstantBufferAt(symbols, block) is not { StructParameters.Length: > 0 } buffer)
            {
                continue;
            }

            foreach (ModuleStructMember field in module.MembersOf(block.StructType))
            {
                if (field.ElementStruct == 0
                    || field.Offset is not uint offset
                    || buffer.StructParameters.FirstOrDefault(declared => declared.Index == offset) is not { } declared)
                {
                    continue;
                }

                foreach (ModuleStructMember member in module.MembersOf(field.ElementStruct))
                {
                    if (member.Name is not null || member.Offset is not uint local)
                    {
                        continue;
                    }

                    NumericShaderParameter? stated = declared.AllNumericMembers.FirstOrDefault(parameter => parameter.Index == declared.Index + local);
                    if (!string.IsNullOrEmpty(stated?.Name))
                    {
                        names.Add(new BoundMemberName(field.ElementStruct, member.Index, stated.Name));
                    }
                }
            }
        }
    }

    private static ConstantBufferParameter? ConstantBufferAt(SerializedProgramData symbols, ModuleBlock block)
    {
        foreach (BufferBindingParameter declared in symbols.BufferBindingParameters)
        {
            if (declared.Index == block.Binding
                && !string.IsNullOrEmpty(declared.Name)
                && symbols.GetSetIdFor(declared.Index, ShaderResourceType.ConstantBuffer, declared.Name) == block.Set)
            {
                return symbols.GetConstantBufferByName(declared.Name);
            }
        }

        return null;
    }
}
