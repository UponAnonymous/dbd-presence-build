using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace DbdPresence.ArchitectureTests;

/// <summary>A P/Invoke declared in an inspected assembly.</summary>
internal sealed record PInvokeDeclaration(
    MethodDefinitionHandle Handle, string DeclaringType, string MethodName, string Module, string EntryPoint, bool IsPublic);

/// <summary>A reference from an inspected assembly to a member of another assembly.</summary>
internal sealed record MemberReferenceInfo(string ParentType, string MemberName);

/// <summary>One decoded IL instruction. <see cref="Operand"/> is an int (ldc.*), string (ldstr), EntityHandle (tokens) or null.</summary>
internal readonly record struct Instruction(ILOpCode OpCode, object? Operand);

/// <summary>A method with a body, its declaring type and its decoded instructions.</summary>
internal sealed record MethodBodyInfo(MethodDefinitionHandle Handle, string DeclaringType, string Name, ImmutableArray<Instruction> Instructions, bool IsLibraryImportWrapper);

/// <summary>
/// Reads an assembly's metadata and IL without loading it, so Windows-targeted assemblies can be
/// inspected on Linux. The IL decoder follows ECMA-335 Partition III operand sizes and is exercised
/// against every method of System.Private.CoreLib by a conformance test.
/// </summary>
internal sealed class AssemblyInspector : IDisposable
{
    /// <summary>The <c>no.</c> prefix (0xFE 0x19), which <see cref="ILOpCode"/> does not name.</summary>
    private const ILOpCode NoPrefix = (ILOpCode)0xFE19;

    private readonly PEReader _peReader;
    private readonly MetadataReader _reader;

    public AssemblyInspector(string path)
        : this(path, new PEReader(File.OpenRead(path)))
    {
    }

    private AssemblyInspector(string path, PEReader peReader)
    {
        Path = path;
        _peReader = peReader;
        _reader = _peReader.GetMetadataReader();
    }

    /// <summary>Opens a managed assembly; returns null (and releases the file) for native DLLs without metadata.</summary>
    public static AssemblyInspector? TryOpen(string path)
    {
        var peReader = new PEReader(File.OpenRead(path));
        if (!peReader.HasMetadata)
        {
            peReader.Dispose();
            return null;
        }

        return new AssemblyInspector(path, peReader);
    }

    public string Path { get; }

    public string AssemblyName => _reader.GetString(_reader.GetAssemblyDefinition().Name);

    public ImmutableArray<string> AssemblyReferences =>
        [.. _reader.AssemblyReferences.Select(h => _reader.GetString(_reader.GetAssemblyReference(h).Name))];

    public ImmutableArray<string> TypeReferences =>
        [.. _reader.TypeReferences.Select(h => FullName(_reader.GetTypeReference(h)))];

    public ImmutableArray<MemberReferenceInfo> MemberReferences =>
        [.. _reader.MemberReferences
            .Select(h => _reader.GetMemberReference(h))
            .Select(m => new MemberReferenceInfo(ParentName(m.Parent), _reader.GetString(m.Name)))];

    /// <summary>Types declared with <c>[ComImport]</c> (classic runtime-callable COM interfaces).</summary>
    public ImmutableArray<string> ComImportTypes =>
        [.. _reader.TypeDefinitions
            .Select(h => _reader.GetTypeDefinition(h))
            .Where(t => (t.Attributes & TypeAttributes.Import) != 0)
            .Select(FullName)];

    public ImmutableArray<PInvokeDeclaration> PInvokes
    {
        get
        {
            var result = ImmutableArray.CreateBuilder<PInvokeDeclaration>();
            foreach (var (type, handle, method) in AllMethods())
            {
                if ((method.Attributes & MethodAttributes.PinvokeImpl) == 0)
                {
                    continue;
                }

                var import = method.GetImport();
                var module = _reader.GetString(_reader.GetModuleReference(import.Module).Name);
                var entry = import.Name.IsNil ? _reader.GetString(method.Name) : _reader.GetString(import.Name);
                var isPublic = (method.Attributes & MethodAttributes.MemberAccessMask) == MethodAttributes.Public;
                result.Add(new PInvokeDeclaration(handle, FullName(type), _reader.GetString(method.Name), module, entry, isPublic));
            }

            return result.ToImmutable();
        }
    }

    /// <summary>Methods carrying <c>[LibraryImport]</c> (source-generated wrappers), with their native entry point.</summary>
    public ImmutableDictionary<MethodDefinitionHandle, string> LibraryImportWrappers
    {
        get
        {
            var result = ImmutableDictionary.CreateBuilder<MethodDefinitionHandle, string>();
            foreach (var (_, handle, method) in AllMethods())
            {
                if (LibraryImportEntryPoint(method) is { } entry)
                {
                    result[handle] = entry;
                }
            }

            return result.ToImmutable();
        }
    }

    /// <summary>Every method that has an IL body, decoded.</summary>
    public IEnumerable<MethodBodyInfo> MethodBodies()
    {
        var wrappers = LibraryImportWrappers;
        foreach (var (type, handle, method) in AllMethods())
        {
            if (method.RelativeVirtualAddress == 0)
            {
                continue;
            }

            var body = _peReader.GetMethodBody(method.RelativeVirtualAddress);
            yield return new MethodBodyInfo(handle, FullName(type), _reader.GetString(method.Name), Decode(body.GetILReader()), wrappers.ContainsKey(handle));
        }
    }

    /// <summary>Resolves a call operand to "Namespace.Type::Member" for messages and matching.</summary>
    public string Describe(EntityHandle handle) => handle.Kind switch
    {
        HandleKind.MethodDefinition => DescribeDefinition((MethodDefinitionHandle)handle),
        HandleKind.MemberReference => DescribeReference((MemberReferenceHandle)handle),
        HandleKind.MethodSpecification => Describe(_reader.GetMethodSpecification((MethodSpecificationHandle)handle).Method),
        _ => handle.Kind.ToString(),
    };

    /// <summary>
    /// The parameter types of a called method as full names (<c>System.Uri</c>, <c>System.String</c>, ...), for member references and
    /// generic method instantiations; empty for anything else. Lets a rule tell <c>Create(Uri, ...)</c> from <c>Create(Stream, ...)</c>.
    /// </summary>
    public IReadOnlyList<string> ParameterTypes(EntityHandle handle)
    {
        if (handle.Kind == HandleKind.MethodSpecification)
        {
            return ParameterTypes(_reader.GetMethodSpecification((MethodSpecificationHandle)handle).Method);
        }

        if (handle.Kind != HandleKind.MemberReference)
        {
            return [];
        }

        var member = _reader.GetMemberReference((MemberReferenceHandle)handle);
        if (member.GetKind() != MemberReferenceKind.Method)
        {
            return [];
        }

        var signature = member.DecodeMethodSignature(new SignatureNames(this), null);
        return [.. signature.ParameterTypes];
    }

    public void Dispose() => _peReader.Dispose();

    private IEnumerable<(TypeDefinition Type, MethodDefinitionHandle Handle, MethodDefinition Method)> AllMethods()
    {
        foreach (var typeHandle in _reader.TypeDefinitions)
        {
            var type = _reader.GetTypeDefinition(typeHandle);
            foreach (var methodHandle in type.GetMethods())
            {
                yield return (type, methodHandle, _reader.GetMethodDefinition(methodHandle));
            }
        }
    }

    private ImmutableArray<Instruction> Decode(BlobReader il)
    {
        var instructions = ImmutableArray.CreateBuilder<Instruction>();
        while (il.RemainingBytes > 0)
        {
            int code = il.ReadByte();
            if (code == 0xFE)
            {
                code = 0xFE00 | il.ReadByte();
            }

            var op = (ILOpCode)code;
            object? operand = null;
            switch (op)
            {
                case ILOpCode.Ldc_i4:
                    operand = il.ReadInt32();
                    break;
                case ILOpCode.Ldc_i4_s:
                    operand = (int)il.ReadSByte();
                    break;
                case >= ILOpCode.Ldc_i4_m1 and <= ILOpCode.Ldc_i4_8:
                    operand = (int)op - (int)ILOpCode.Ldc_i4_0;
                    break;
                case ILOpCode.Ldstr:
                    operand = _reader.GetUserString(MetadataTokens.UserStringHandle(il.ReadInt32() & 0x00FFFFFF));
                    break;
                case ILOpCode.Switch:
                    // Read the count first: `il.Offset += il.ReadInt32() * 4` would add to the offset
                    // captured before ReadInt32 advanced it, desynchronising the stream.
                    var targets = il.ReadInt32();
                    il.Offset += targets * 4;
                    break;
                default:
                    var size = OperandSize(op);
                    if (size == 4 && IsTokenOperand(op))
                    {
                        operand = MetadataTokens.EntityHandle(il.ReadInt32());
                    }
                    else
                    {
                        il.Offset += size;
                    }

                    break;
            }

            instructions.Add(new Instruction(op, operand));
        }

        return instructions.ToImmutable();
    }

    private static bool IsTokenOperand(ILOpCode op) => op is ILOpCode.Call or ILOpCode.Callvirt or ILOpCode.Newobj
        or ILOpCode.Ldftn or ILOpCode.Ldvirtftn or ILOpCode.Jmp or ILOpCode.Ldtoken or ILOpCode.Ldfld or ILOpCode.Ldflda
        or ILOpCode.Stfld or ILOpCode.Ldsfld or ILOpCode.Ldsflda or ILOpCode.Stsfld;

    private static int OperandSize(ILOpCode op) => op switch
    {
        ILOpCode.Br_s or ILOpCode.Brfalse_s or ILOpCode.Brtrue_s or ILOpCode.Beq_s or ILOpCode.Bge_s
            or ILOpCode.Bgt_s or ILOpCode.Ble_s or ILOpCode.Blt_s or ILOpCode.Bne_un_s or ILOpCode.Bge_un_s
            or ILOpCode.Bgt_un_s or ILOpCode.Ble_un_s or ILOpCode.Blt_un_s or ILOpCode.Leave_s
            or ILOpCode.Ldarg_s or ILOpCode.Ldarga_s or ILOpCode.Starg_s or ILOpCode.Ldloc_s
            or ILOpCode.Ldloca_s or ILOpCode.Stloc_s or ILOpCode.Unaligned or NoPrefix => 1,
        ILOpCode.Ldarg or ILOpCode.Ldarga or ILOpCode.Starg or ILOpCode.Ldloc or ILOpCode.Ldloca
            or ILOpCode.Stloc => 2,
        ILOpCode.Ldc_r4 or ILOpCode.Br or ILOpCode.Brfalse or ILOpCode.Brtrue or ILOpCode.Beq or ILOpCode.Bge
            or ILOpCode.Bgt or ILOpCode.Ble or ILOpCode.Blt or ILOpCode.Bne_un or ILOpCode.Bge_un
            or ILOpCode.Bgt_un or ILOpCode.Ble_un or ILOpCode.Blt_un or ILOpCode.Leave or ILOpCode.Calli
            or ILOpCode.Call or ILOpCode.Callvirt or ILOpCode.Newobj or ILOpCode.Ldftn or ILOpCode.Ldvirtftn
            or ILOpCode.Jmp or ILOpCode.Ldfld or ILOpCode.Ldflda or ILOpCode.Stfld or ILOpCode.Ldsfld
            or ILOpCode.Ldsflda or ILOpCode.Stsfld or ILOpCode.Ldstr or ILOpCode.Ldtoken or ILOpCode.Cpobj
            or ILOpCode.Ldobj or ILOpCode.Castclass or ILOpCode.Isinst or ILOpCode.Unbox or ILOpCode.Stobj
            or ILOpCode.Box or ILOpCode.Newarr or ILOpCode.Ldelema or ILOpCode.Ldelem or ILOpCode.Stelem
            or ILOpCode.Unbox_any or ILOpCode.Refanyval or ILOpCode.Mkrefany or ILOpCode.Initobj
            or ILOpCode.Constrained or ILOpCode.Sizeof => 4,
        ILOpCode.Ldc_i8 or ILOpCode.Ldc_r8 => 8,
        _ => 0,
    };

    private string? LibraryImportEntryPoint(MethodDefinition method)
    {
        foreach (var attributeHandle in method.GetCustomAttributes())
        {
            var attribute = _reader.GetCustomAttribute(attributeHandle);
            if (AttributeTypeName(attribute) != "System.Runtime.InteropServices.LibraryImportAttribute")
            {
                continue;
            }

            var value = attribute.DecodeValue(new StringOnlyAttributeTypeProvider());
            foreach (var named in value.NamedArguments)
            {
                if (named.Name == "EntryPoint" && named.Value is string entry)
                {
                    return entry;
                }
            }

            return _reader.GetString(method.Name);
        }

        return null;
    }

    private string AttributeTypeName(CustomAttribute attribute) => attribute.Constructor.Kind switch
    {
        HandleKind.MemberReference => ParentName(_reader.GetMemberReference((MemberReferenceHandle)attribute.Constructor).Parent),
        HandleKind.MethodDefinition => FullName(_reader.GetTypeDefinition(_reader.GetMethodDefinition((MethodDefinitionHandle)attribute.Constructor).GetDeclaringType())),
        _ => string.Empty,
    };

    private string DescribeDefinition(MethodDefinitionHandle handle)
    {
        var method = _reader.GetMethodDefinition(handle);
        return $"{FullName(_reader.GetTypeDefinition(method.GetDeclaringType()))}::{_reader.GetString(method.Name)}";
    }

    private string DescribeReference(MemberReferenceHandle handle)
    {
        var member = _reader.GetMemberReference(handle);
        return $"{ParentName(member.Parent)}::{_reader.GetString(member.Name)}";
    }

    private string FullName(TypeReference type)
    {
        var name = _reader.GetString(type.Name);
        if (type.ResolutionScope.Kind == HandleKind.TypeReference)
        {
            return $"{FullName(_reader.GetTypeReference((TypeReferenceHandle)type.ResolutionScope))}+{name}";
        }

        var ns = _reader.GetString(type.Namespace);
        return ns.Length == 0 ? name : $"{ns}.{name}";
    }

    private string FullName(TypeDefinition type)
    {
        var name = _reader.GetString(type.Name);
        var declaring = type.GetDeclaringType();
        if (!declaring.IsNil)
        {
            return $"{FullName(_reader.GetTypeDefinition(declaring))}+{name}";
        }

        var ns = _reader.GetString(type.Namespace);
        return ns.Length == 0 ? name : $"{ns}.{name}";
    }

    internal string TypeName(MetadataReader reader, EntityHandle handle) => handle.Kind switch
    {
        HandleKind.TypeReference => FullName(reader.GetTypeReference((TypeReferenceHandle)handle)),
        HandleKind.TypeDefinition => FullName(reader.GetTypeDefinition((TypeDefinitionHandle)handle)),
        _ => handle.Kind.ToString(),
    };

    private string ParentName(EntityHandle parent) => parent.Kind switch
    {
        HandleKind.TypeReference => FullName(_reader.GetTypeReference((TypeReferenceHandle)parent)),
        HandleKind.TypeDefinition => FullName(_reader.GetTypeDefinition((TypeDefinitionHandle)parent)),
        HandleKind.TypeSpecification => GenericParentName((TypeSpecificationHandle)parent),
        HandleKind.MethodDefinition => DescribeDefinition((MethodDefinitionHandle)parent),
        _ => parent.Kind.ToString(),
    };

    private string GenericParentName(TypeSpecificationHandle handle)
    {
        // Generic instantiations encode the open type first; decode just enough to name it.
        var blob = _reader.GetBlobReader(_reader.GetTypeSpecification(handle).Signature);
        if (blob.ReadSignatureTypeCode() == SignatureTypeCode.GenericTypeInstance)
        {
            blob.ReadSignatureTypeCode();
            return ParentName(blob.ReadTypeHandle());
        }

        return "TypeSpecification";
    }
}

/// <summary>Names the types of a method signature; composite types are reduced to a readable approximation.</summary>
internal sealed class SignatureNames(AssemblyInspector owner) : ISignatureTypeProvider<string, object?>
{
    public string GetPrimitiveType(PrimitiveTypeCode typeCode) => "System." + typeCode;

    public string GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind) => owner.TypeName(reader, handle);

    public string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind) => owner.TypeName(reader, handle);

    public string GetTypeFromSpecification(MetadataReader reader, object? genericContext, TypeSpecificationHandle handle, byte rawTypeKind) => "TypeSpecification";

    public string GetSZArrayType(string elementType) => elementType + "[]";

    public string GetArrayType(string elementType, ArrayShape shape) => elementType + "[,]";

    public string GetByReferenceType(string elementType) => elementType + "&";

    public string GetPointerType(string elementType) => elementType + "*";

    public string GetPinnedType(string elementType) => elementType;

    public string GetModifiedType(string modifier, string unmodifiedType, bool isRequired) => unmodifiedType;

    public string GetGenericInstantiation(string genericType, System.Collections.Immutable.ImmutableArray<string> typeArguments) => genericType;

    public string GetGenericMethodParameter(object? genericContext, int index) => "!!" + index;

    public string GetGenericTypeParameter(object? genericContext, int index) => "!" + index;

    public string GetFunctionPointerType(MethodSignature<string> signature) => "fnptr";
}

/// <summary>Minimal attribute-signature decoder: enough to read string and primitive arguments.</summary>
internal sealed class StringOnlyAttributeTypeProvider : ICustomAttributeTypeProvider<object?>
{
    public object? GetPrimitiveType(PrimitiveTypeCode typeCode) => typeCode;

    public object? GetSystemType() => typeof(Type);

    public object? GetSZArrayType(object? elementType) => elementType;

    public object? GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind) => handle;

    public object? GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind) => handle;

    public object? GetTypeFromSerializedName(string name) => name;

    public PrimitiveTypeCode GetUnderlyingEnumType(object? type) => PrimitiveTypeCode.Int32;

    public bool IsSystemType(object? type) => type is Type t && t == typeof(Type);
}
