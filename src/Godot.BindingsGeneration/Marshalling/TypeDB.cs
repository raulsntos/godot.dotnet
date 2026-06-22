using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using Godot.BindingsGeneration.Marshallers;
using Godot.BindingsGeneration.Reflection;

namespace Godot.BindingsGeneration;

internal sealed class TypeDB
{
    /// <summary>
    /// Mapping between the engine type names and the managed type info.
    /// </summary>
    private readonly Dictionary<string, TypeInfo> _typeByEngineName = [];

    /// <summary>
    /// Mapping between the engine type name metadata and the managed type info.
    /// </summary>
    private readonly Dictionary<string, TypeInfo> _typeByEngineMetaName = [];

    /// <summary>
    /// Mapping between the managed type and the unmanaged type used to marshal it.
    /// </summary>
    private readonly Dictionary<TypeInfo, TypeInfo> _unmanagedTypes = [];

    /// <summary>
    /// Mapping between the managed or unmanaged type and the ptr marshaller writer that can convert it.
    /// </summary>
    private readonly Dictionary<TypeInfo, PtrMarshallerWriter> _ptrMarshallerWriters = [];

    /// <summary>
    /// Mapping between the managed or unmanaged type and the variant marshaller writer that can convert it.
    /// </summary>
    private readonly Dictionary<TypeInfo, VariantMarshallerWriter> _variantMarshallerWriters = [];

    /// <summary>
    /// Mapping between the managed or unmanaged type and the default value parser.
    /// </summary>
    private readonly Dictionary<TypeInfo, DefaultValueParser> _defaultValueParsers = [];

    /// <summary>
    /// Mapping between the engine member name and the managed member name for each type.
    /// The key of the outer dictionary is the managed type, the key of the inner dictionary
    /// is the engine member name, and the value is the fully qualified managed member name.
    /// </summary>
    private readonly Dictionary<TypeInfo, Dictionary<string, string>> _memberMappingByType = [];

    /// <summary>
    /// Mapping between the engine member name and the managed member name for each type.
    /// The key of the outer dictionary is the managed type, the key of the inner dictionary
    /// is the engine member name, and the value is a function that returns the fully qualified managed member name.
    /// </summary>
    /// <remarks>
    /// This is used when registering the mapping early, before the member is fully generated.
    /// In this case, the member or its containing type may still be renamed, so we don't know
    /// the fully qualified name yet.
    /// </remarks>
    private readonly Dictionary<TypeInfo, Dictionary<string, Func<string>>> _lazyMemberMappingByType = [];

    /// <summary>
    /// Mapping between the engine member name and the managed member name for types that
    /// don't exist in TypeDB, like global functions or constants.
    /// The key is the fully qualified engine member name, and the value is the fully qualified
    /// managed member name.
    /// </summary>
    private readonly Dictionary<string, string> _globalMemberMapping = [];

    /// <summary>
    /// Register the type that will be used for the given engine name.
    /// </summary>
    /// <param name="engineTypeName">The name of the type in the Godot engine.</param>
    /// <param name="type">The type that will be used in C#.</param>
    public void RegisterTypeName(string engineTypeName, TypeInfo type)
    {
        if (_typeByEngineName.ContainsKey(engineTypeName))
        {
            throw new ArgumentException($"Type for engine name '{engineTypeName}' already registered.", nameof(engineTypeName));
        }

        _typeByEngineName[engineTypeName] = type;
    }

    /// <summary>
    /// Register the type that will be used for the given engine name and metadata.
    /// </summary>
    /// <param name="engineTypeName">The name of the type in the Godot engine.</param>
    /// <param name="engineTypeMeta">The metadata for the type used by the Godot engine.</param>
    /// <param name="type">The type that will be used in C#.</param>
    public void RegisterTypeMetaName(string engineTypeName, string engineTypeMeta, TypeInfo type)
    {
        string key = $"{engineTypeName}:{engineTypeMeta}";

        if (_typeByEngineMetaName.ContainsKey(key))
        {
            throw new ArgumentException($"Type for engine name '{engineTypeName}' (metadata: '{engineTypeMeta}') already registered.", nameof(engineTypeMeta));
        }

        _typeByEngineMetaName[key] = type;
    }

    /// <summary>
    /// Register a type mapping to itself, the type is used in public API and also acts
    /// as the unmanaged type that it's converted to when used with the GDExtension API
    /// (so no conversion is actually needed).
    /// </summary>
    /// <param name="type">The unmanaged type that is exposed to the bindings consumers.</param>
    public void RegisterUnmanagedType(TypeInfo type)
    {
        RegisterUnmanagedType(type, type);
    }

    /// <summary>
    /// Register a type mapping between a type that is used in public API and the
    /// unmanaged type that it's converted to when used with the GDExtension API.
    /// </summary>
    /// <param name="type">The type that is exposed to the bindings consumers.</param>
    /// <param name="unmanagedType">The unmanaged type that is used with the GDExtension API.</param>
    public void RegisterUnmanagedType(TypeInfo type, TypeInfo unmanagedType)
    {
        if (_unmanagedTypes.TryGetValue(type, out TypeInfo? registeredUnmanagedType))
        {
            if (unmanagedType == registeredUnmanagedType)
            {
                // Changing an existent mapping is an error, but in this case the unmanaged type is the same.
                // This means we're registering the same mapping, so even though is meaningless to registered
                // the same mapping multiple times we'll allow it out of convenience.
                return;
            }

            throw new ArgumentException($"Marshalling for type '{type.FullName}' already registered.", nameof(type));
        }

        Debug.Assert(!unmanagedType.IsReferenceType, $"Type '{unmanagedType.FullName}' can't be registered as an unmanaged type because it's a reference type.");

        _unmanagedTypes[type] = unmanagedType;
    }

    public void RegisterPtrMarshaller(TypeInfo type, PtrMarshallerWriter marshaller)
    {
        if (_ptrMarshallerWriters.ContainsKey(type))
        {
            throw new ArgumentException($"Ptr marshalling for type '{type.FullName}' already registered.", nameof(type));
        }

        _ptrMarshallerWriters[type] = marshaller;
    }

    public void RegisterVariantMarshaller(TypeInfo type, VariantMarshallerWriter marshaller)
    {
        if (_variantMarshallerWriters.ContainsKey(type))
        {
            throw new ArgumentException($"Variant marshalling for type '{type.FullName}' already registered.", nameof(type));
        }

        _variantMarshallerWriters[type] = marshaller;
    }

    public void RegisterDefaultValueParser(TypeInfo type, DefaultValueParser defaultValueParser)
    {
        if (_defaultValueParsers.ContainsKey(type))
        {
            throw new ArgumentException($"Default value parser for type '{type.FullName}' already registered.", nameof(type));
        }

        _defaultValueParsers[type] = defaultValueParser;
    }

    /// <summary>
    /// Registers a mapping between the engine member name and the managed member name for a given type.
    /// </summary>
    /// <remarks>
    /// The <paramref name="type"/> must be registered in <see cref="TypeDB"/> as the managed type
    /// that represents the engine type that contains the member, so it can be found when looking up
    /// the member mapping. If the type may be different, use <see cref="RegisterGlobalMemberMapping"/>
    /// instead.
    /// </remarks>
    /// <param name="type">The type that contains the member.</param>
    /// <param name="engineMemberName">The name of the member in the engine.</param>
    /// <param name="member">The managed member info.</param>
    /// <exception cref="ArgumentException">Member mapping already registered.</exception>
    public void RegisterMemberMapping(TypeInfo type, string engineMemberName, MemberInfo member)
    {
        Func<string> lazyFullyQualifiedMemberName;

        if (member is MethodInfo method)
        {
            // If the member is a method, add parameters to disambiguate overloads.
            lazyFullyQualifiedMemberName = () =>
            {
                var sb = new StringBuilder();
                sb.Append(type.FullNameWithGlobal);
                sb.Append('.');
                sb.Append(method.Name);
                sb.Append('(');
                for (int i = 0; i < method.Parameters.Count; i++)
                {
                    ParameterInfo? parameter = method.Parameters[i];
                    sb.Append(parameter.Type.FullNameWithGlobal);
                    if (i < method.Parameters.Count - 1)
                    {
                        sb.Append(", ");
                    }
                }
                sb.Append(')');
                return sb.ToString();
            };
        }
        else
        {
            lazyFullyQualifiedMemberName = () => $"{type.FullNameWithGlobal}.{member.Name}";
        }

        RegisterLazyMemberMapping(type, engineMemberName, lazyFullyQualifiedMemberName);

        if (type.IsEnum && type.ContainingType is not null)
        {
            // Enum members are also registered in the containing type, because the engine
            // allows accessing them directly as if they were constants in the containing type.
            RegisterLazyMemberMapping(type.ContainingType, engineMemberName, lazyFullyQualifiedMemberName);
        }
    }

    private void RegisterLazyMemberMapping(TypeInfo type, string engineMemberName, Func<string> lazyFullyQualifiedMemberName)
    {
        if (!_lazyMemberMappingByType.TryGetValue(type, out var memberMapping))
        {
            _lazyMemberMappingByType[type] = memberMapping = [];
        }

        // There should be no overlapping names even if the member kind is different,
        // the engine doesn't allow it and there's no method overload support either.
        if (memberMapping.ContainsKey(engineMemberName))
        {
            throw new ArgumentException($"Member mapping for member '{engineMemberName}' on type '{type.FullName}' already registered.", nameof(engineMemberName));
        }

        memberMapping[engineMemberName] = lazyFullyQualifiedMemberName;
    }

    /// <summary>
    /// Registers a mapping between the engine member name and the managed member name for a given type.
    /// </summary>
    /// <remarks>
    /// The <paramref name="type"/> must be registered in <see cref="TypeDB"/> as the managed type
    /// that represents the engine type that contains the member, so it can be found when looking up
    /// the member mapping. If the type may be different, use <see cref="RegisterGlobalMemberMapping"/>
    /// instead.
    /// </remarks>
    /// <param name="type">The type that contains the member.</param>
    /// <param name="engineMemberName">The name of the member in the engine.</param>
    /// <param name="fullyQualifiedMemberName">
    /// The fully qualified name of the managed member. This allows specifying a completely different
    /// type than <paramref name="type"/> for the member, which is useful for extension methods or
    /// methods that are implemented in a different type than the one that contains the member in the engine.
    /// </param>
    /// <exception cref="ArgumentException">Member mapping already registered.</exception>
    public void RegisterMemberMapping(TypeInfo type, string engineMemberName, string fullyQualifiedMemberName)
    {
        if (!_memberMappingByType.TryGetValue(type, out var memberMapping))
        {
            _memberMappingByType[type] = memberMapping = [];
        }

        // There should be no overlapping names even if the member kind is different,
        // the engine doesn't allow it and there's no method overload support either.
        if (memberMapping.ContainsKey(engineMemberName))
        {
            throw new ArgumentException($"Member mapping for member '{engineMemberName}' on type '{type.FullName}' already registered.", nameof(engineMemberName));
        }

        memberMapping[engineMemberName] = fullyQualifiedMemberName;
    }

    /// <summary>
    /// Registers a mapping between the engine member name and the managed member name for types that
    /// are not tied to a specific type, such as global functions or operators.
    /// </summary>
    /// <remarks>
    /// Unlike <see cref="RegisterMemberMapping(TypeInfo, string, string)"/>, this method allows specifying
    /// any type for the member, so it can be used for members that are not tied to a specific type or types
    /// that aren't registered in <see cref="TypeDB"/>. It can also be used to implement hardcoded special
    /// cases that will take precedence over looking up the member mapping for a specific type.
    /// Prefer <see cref="RegisterMemberMapping(TypeInfo, string, string)"/> when the type is known and registered
    /// in <see cref="TypeDB"/>, so it can participate in hierarchy lookups.
    /// </remarks>
    /// <param name="fullyQualifiedEngineMemberName">The fully qualified name of the engine member.</param>
    /// <param name="fullyQualifiedMemberName">The fully qualified name of the managed member.</param>
    /// <exception cref="ArgumentException">Member mapping already registered.</exception>
    public void RegisterGlobalMemberMapping(string fullyQualifiedEngineMemberName, string fullyQualifiedMemberName)
    {
        // There should be no overlapping names even if the member kind is different,
        // the engine doesn't allow it and there's no method overload support either.
        if (_globalMemberMapping.ContainsKey(fullyQualifiedEngineMemberName))
        {
            throw new ArgumentException($"Member mapping for member '{fullyQualifiedEngineMemberName}' already registered.", nameof(fullyQualifiedEngineMemberName));
        }

        _globalMemberMapping[fullyQualifiedEngineMemberName] = fullyQualifiedMemberName;

        // HARDCODED: Global APIs are registered in a "@GlobalScope" class in the engine, but the documentation
        // sometimes omits the "@GlobalScope" prefix when referencing the member name. We register the mapping
        // without the prefix as well, so it can be found when looking up the member mapping without the prefix.
        if (fullyQualifiedEngineMemberName.StartsWith("@GlobalScope.", StringComparison.Ordinal))
        {
            string engineMemberNameWithoutGlobalScope = fullyQualifiedEngineMemberName.Substring("@GlobalScope.".Length);
            if (!_globalMemberMapping.ContainsKey(engineMemberNameWithoutGlobalScope))
            {
                _globalMemberMapping[engineMemberNameWithoutGlobalScope] = fullyQualifiedMemberName;
            }
        }
    }

    public TypeInfo GetTypeFromEngineName(ReadOnlySpan<char> engineTypeName)
    {
        if (!TryGetTypeFromEngineName(engineTypeName, out var type))
        {
            throw new ArgumentException($"Type for engine name '{engineTypeName}' not found.", nameof(engineTypeName));
        }

        return type;
    }

    public TypeInfo GetTypeFromEngineName(ReadOnlySpan<char> engineTypeName, string? engineTypeMeta)
    {
        if (!TryGetTypeFromEngineName(engineTypeName, engineTypeMeta, out var type))
        {
            throw new InvalidOperationException($"Type for engine name '{engineTypeName}' (metadata: '{engineTypeMeta}') not found.");
        }

        return type;
    }

    public bool TryGetTypeFromEngineName(ReadOnlySpan<char> engineTypeName, [NotNullWhen(true)] out TypeInfo? type)
    {
        return TryGetTypeFromEngineName(engineTypeName, null, out type);
    }

    public bool TryGetTypeFromEngineName(ReadOnlySpan<char> engineTypeName, string? engineTypeMeta, [NotNullWhen(true)] out TypeInfo? type)
    {
        if (engineTypeMeta is not null && _typeByEngineMetaName.TryGetValue($"{engineTypeName}:{engineTypeMeta}", out type))
        {
            return true;
        }

        var typeByEngineNameLookup = _typeByEngineName.GetAlternateLookup<ReadOnlySpan<char>>();
        if (typeByEngineNameLookup.TryGetValue(engineTypeName, out type))
        {
            return true;
        }

        // Special types.

        const string EnumTypePrefix = "enum::";
        if (engineTypeName.StartsWith(EnumTypePrefix, StringComparison.Ordinal))
        {
            return TryGetTypeFromEngineName(engineTypeName.Slice(EnumTypePrefix.Length), out type);
        }
        const string BitfieldTypePrefix = "bitfield::";
        if (engineTypeName.StartsWith(BitfieldTypePrefix, StringComparison.Ordinal))
        {
            return TryGetTypeFromEngineName(engineTypeName.Slice(BitfieldTypePrefix.Length), out type);
        }

        const string TypedArrayTypePrefix = "typedarray::";
        if (engineTypeName.StartsWith(TypedArrayTypePrefix, StringComparison.Ordinal))
        {
            if (!TryGetTypeFromEngineName(engineTypeName.Slice(TypedArrayTypePrefix.Length), out var elementType))
            {
                // The element type could not be found, fallback to a non-generic Array.
                Console.Error.WriteLine($"Element type for array type '{engineTypeName}' not found, falling back to non-generic Array.");
                type = KnownTypes.GodotArray;
                return true;
            }

            // Register type and marshalling information.
            type = KnownTypes.GodotArrayOf(elementType);
            RegisterTypeName(engineTypeName.ToString(), type);
            RegisterUnmanagedType(type, KnownTypes.NativeGodotArray);
            return true;
        }

        if (engineTypeName.Contains('*'))
        {
            // It's a pointer type.
            return TryGetTypeInfoForPointer(engineTypeName, out type);
        }

        return false;
    }

    /// <summary>
    /// Get the unmanaged type that <paramref name="type"/> needs to be converted to
    /// in order to use it with the GDExtension API.
    /// </summary>
    /// <param name="type">The type that is exposed to the bindings consumers.</param>
    public TypeInfo GetUnmanagedType(TypeInfo type)
    {
        if (!TryGetUnmanagedType(type, out TypeInfo? unmanagedType))
        {
            throw new ArgumentException($"Type '{type.FullName}' can't be marshalled. Unmanaged type not found.", nameof(type));
        }

        return unmanagedType;
    }

    public bool TryGetUnmanagedType(TypeInfo type, [NotNullWhen(true)] out TypeInfo? unmanagedType)
    {
        if (!_unmanagedTypes.TryGetValue(type, out unmanagedType))
        {
            // Enum marshalling is not registered because it's easy to check if a type is an enum
            // and just return the same marshalling information for all of them.
            if (type.IsEnum)
            {
                unmanagedType = KnownTypes.SystemInt64;
                return true;
            }

            // Nullable<T> is not registered because it's easy to check if a type is nullable
            // and just return the same marshalling information for all of them.
            if (type.IsGenericType && type.GenericTypeDefinition == KnownTypes.Nullable)
            {
                if (TryGetUnmanagedType(type.GenericTypeArguments[0], out unmanagedType))
                {
                    return true;
                }
            }

            // Pointer types should already be unmanaged.
            if (type.IsPointerType)
            {
                unmanagedType = type;
                return true;
            }

            // The base type may have registered marshalling information, we'll use it as a fallback
            // and try to convert to the original type, if it fails it will result in a cast exception.
            if (type.BaseType is not null)
            {
                return TryGetUnmanagedType(type.BaseType, out unmanagedType);
            }

            return false;
        }

        return true;
    }

    /// <summary>
    /// Try to get the <see cref="TypeInfo"/> that represents the pointer type
    /// from the given engine name that comes from Godot's API JSON dump.
    /// </summary>
    /// <param name="engineTypeName">Pointer name in C/C++ syntax.</param>
    /// <param name="pointerType">The type that represents the pointer type.</param>
    /// <returns>
    /// Whether a <see cref="TypeInfo"/> could be created for the pointer type name.
    /// </returns>
    /// <exception cref="InvalidOperationException">
    /// The pointer name is malformed.
    /// </exception>
    private bool TryGetTypeInfoForPointer(ReadOnlySpan<char> engineTypeName, [NotNullWhen(true)] out TypeInfo? pointerType)
    {
        Debug.Assert(engineTypeName.Contains('*'));
        var remaining = engineTypeName;

        if (remaining.StartsWith("const "))
        {
            // Constant pointers can't be represented in C# so we'll just ignore their const-ness.
            remaining = remaining["const ".Length..];
        }

        int idx = remaining.IndexOfAny(' ', '*');
        var pointedAtName = remaining[..idx];
        Debug.Assert(!pointedAtName.Contains('*') && !pointedAtName.Contains(' '));

        string enginePointeeName = pointedAtName.ToString();
        if (!TryGetTypeFromEngineName(enginePointeeName, enginePointeeName, out var pointedAtType))
        {
            // Couldn't find the type that the pointer points to.
            if (pointedAtName.SequenceEqual("void"))
            {
                pointedAtType = KnownTypes.SystemVoid;
            }
            else
            {
                pointerType = null;
                return false;
            }
        }

        remaining = remaining[idx..].TrimStart();

        // All remaining characters must be '*'.
        if (remaining.IndexOfAnyExcept('*') != -1)
        {
            throw new InvalidOperationException($"Malformed pointer type name '{engineTypeName}'.");
        }

        int indirectionLevel = remaining.Length;
        Debug.Assert(indirectionLevel > 0);

        pointerType = pointedAtType;
        while (indirectionLevel-- > 0)
        {
            pointerType = pointerType.MakePointerType();
        }
        return true;
    }

    public PtrMarshallerWriter GetPtrMarshaller(TypeInfo type)
    {
        if (!TryGetPtrMarshaller(type, out var marshaller))
        {
            throw new ArgumentException($"Type '{type.FullName}' can't be marshalled. Ptr marshaller not found.", nameof(type));
        }

        return marshaller;
    }

    public bool TryGetPtrMarshaller(TypeInfo type, [NotNullWhen(true)] out PtrMarshallerWriter? marshaller)
    {
        if (!_ptrMarshallerWriters.TryGetValue(type, out marshaller))
        {
            // Enum marshalling is not registered because it's easy to check if a type is an enum
            // and just return the same marshalling information for all of them.
            if (type is EnumInfo enumType)
            {
                Debug.Assert(type != KnownTypes.SystemEnum);
                marshaller = new EnumPtrMarshallerWriter(enumType);
                return true;
            }

            // Nullable<T> is not registered because it's easy to check if a type is nullable
            // and just return the same marshalling information for all of them.
            if (type.IsGenericType && type.GenericTypeDefinition == KnownTypes.Nullable)
            {
                if (TryGetPtrMarshaller(type.GenericTypeArguments[0], out var underlyingMarshaller))
                {
                    marshaller = new NullablePtrMarshallerWriter(type, underlyingMarshaller);
                    return true;
                }
            }

            // If the type is a constructed generic, try looking for a marshaller
            // registered for its generic type definition.
            if (type.IsGenericType && !type.IsGenericTypeDefinition)
            {
                if (TryGetPtrMarshaller(type.GenericTypeDefinition, out marshaller))
                {
                    return true;
                }
            }

            if (type.IsPointerType)
            {
                marshaller = new PtrPtrMarshallerWriter(type);
                return true;
            }

            // The base type may have registered marshalling information, we'll use it as a fallback
            // and try to convert to the original type, if it fails it will result in a cast exception.
            if (type.BaseType is not null)
            {
                return TryGetPtrMarshaller(type.BaseType, out marshaller);
            }

            return false;
        }

        return true;
    }

    public VariantMarshallerWriter GetVariantMarshaller(TypeInfo type)
    {
        if (!TryGetVariantMarshaller(type, out var marshaller))
        {
            throw new ArgumentException($"Type '{type.FullName}' can't be marshalled. Variant marshaller not found.", nameof(type));
        }

        return marshaller;
    }

    public bool TryGetVariantMarshaller(TypeInfo type, [NotNullWhen(true)] out VariantMarshallerWriter? marshaller)
    {
        if (!_variantMarshallerWriters.TryGetValue(type, out marshaller))
        {
            // Enum marshalling is not registered because it's easy to check if a type is an enum
            // and just return the same marshalling information for all of them.
            if (type is EnumInfo enumType)
            {
                Debug.Assert(type != KnownTypes.SystemEnum);
                marshaller = new EnumVariantMarshallerWriter(enumType);
                return true;
            }

            // Nullable<T> is not registered because it's easy to check if a type is nullable
            // and just return the same marshalling information for all of them.
            if (type.IsGenericType && type.GenericTypeDefinition == KnownTypes.Nullable)
            {
                if (TryGetVariantMarshaller(type.GenericTypeArguments[0], out marshaller))
                {
                    return true;
                }
            }

            // If the type is a constructed generic, try looking for a marshaller
            // registered for its generic type definition.
            if (type.IsGenericType && !type.IsGenericTypeDefinition)
            {
                if (TryGetVariantMarshaller(type.GenericTypeDefinition, out marshaller))
                {
                    return true;
                }
            }

            // The base type may have registered marshalling information, we'll use it as a fallback
            // and try to convert to the original type, if it fails it will result in a cast exception.
            if (type.BaseType is not null)
            {
                return TryGetVariantMarshaller(type.BaseType, out marshaller);
            }

            return false;
        }

        return true;
    }

    public DefaultValueParser GetDefaultValueParser(TypeInfo type)
    {
        if (!TryGetDefaultValueParser(type, out DefaultValueParser? defaultValueParser))
        {
            throw new ArgumentException($"Type '{type.FullName}' can't be used for default values. Default value parser not found.", nameof(type));
        }

        return defaultValueParser;
    }

    public bool TryGetDefaultValueParser(TypeInfo type, [NotNullWhen(true)] out DefaultValueParser? defaultValueParser)
    {
        if (!_defaultValueParsers.TryGetValue(type, out defaultValueParser))
        {
            // Enum parser is not registered because it's easy to check if a type is an enum
            // and just return the same default value parser for all of them.
            if (type.IsEnum)
            {
                Debug.Assert(type != KnownTypes.SystemEnum);
                return TryGetDefaultValueParser(KnownTypes.SystemEnum, out defaultValueParser);
            }

            // Nullable<T> is not registered because it's easy to check if a type is nullable
            // and just return the same default value parser for all of them.
            if (type.IsGenericType && type.GenericTypeDefinition == KnownTypes.Nullable)
            {
                if (TryGetDefaultValueParser(type.GenericTypeArguments[0], out defaultValueParser))
                {
                    return true;
                }
            }

            // If the type is a constructed generic, try looking for a parser
            // registered for its generic type definition.
            if (type.IsGenericType && !type.IsGenericTypeDefinition)
            {
                return TryGetDefaultValueParser(type.GenericTypeDefinition, out defaultValueParser);
            }

            // The base type may have registered a default value parser, we'll use it as a fallback.
            if (type.BaseType is not null)
            {
                return TryGetDefaultValueParser(type.BaseType, out defaultValueParser);
            }

            return false;
        }

        return true;
    }

    public string GetDefaultValueExpression(TypeInfo type, string? engineDefaultValueExpression)
    {
        if (!TryGetDefaultValueExpression(type, engineDefaultValueExpression, out string? defaultValueExpression))
        {
            throw new ArgumentException($"Value '{engineDefaultValueExpression}' can't be used as a default value for type '{type.FullName}'.", nameof(type));
        }

        return defaultValueExpression;
    }

    public bool TryGetDefaultValueExpression(TypeInfo type, string? engineDefaultValueExpression, [NotNullWhen(true)] out string? defaultValueExpression)
    {
        if (string.IsNullOrEmpty(engineDefaultValueExpression))
        {
            defaultValueExpression = null;
            return false;
        }

        if (TryGetDefaultValueParser(type, out var parser))
        {
            defaultValueExpression = parser.Parse(engineDefaultValueExpression, this);
            return true;
        }

        // Special case that works for all reference types.
        if (type.IsReferenceType && engineDefaultValueExpression == "null")
        {
            defaultValueExpression = "default";
            return true;
        }

        defaultValueExpression = null;
        return false;
    }

    public bool TryGetGlobalMemberMapping(ReadOnlySpan<char> fullyQualifiedEngineMemberName, [NotNullWhen(true)] out string? fullyQualifiedMemberName)
    {
        var lookup = _globalMemberMapping.GetAlternateLookup<ReadOnlySpan<char>>();

        if (lookup.TryGetValue(fullyQualifiedEngineMemberName, out fullyQualifiedMemberName))
        {
            return true;
        }

        // HARDCODED: Global APIs are registered in a "@GlobalScope" class in the engine, so the documentation
        // sometimes uses the "@GlobalScope" prefix when fully-qualifying the member name. We may still be able
        // to find the member if we remove the prefix.
        if (fullyQualifiedEngineMemberName.StartsWith("@GlobalScope.", StringComparison.Ordinal))
        {
            fullyQualifiedEngineMemberName = fullyQualifiedEngineMemberName.Slice("@GlobalScope.".Length);
            if (lookup.TryGetValue(fullyQualifiedEngineMemberName, out fullyQualifiedMemberName))
            {
                return true;
            }
        }

        return false;
    }

    public bool TryGetMemberMapping(TypeInfo type, ReadOnlySpan<char> engineMemberName, [NotNullWhen(true)] out string? fullyQualifiedMemberName)
    {
        // Try to find the mapping in the global member mapping first, because it will be faster
        // than iterating the type hierarchy and it allows hardcoding for special cases.
        if (TryGetGlobalMemberMapping(engineMemberName, out fullyQualifiedMemberName))
        {
            return true;
        }

        return TryGetMemberMappingCore(type, engineMemberName, out fullyQualifiedMemberName);
    }

    private bool TryGetMemberMappingCore(TypeInfo type, ReadOnlySpan<char> engineMemberName, [NotNullWhen(true)] out string? fullyQualifiedMemberName)
    {
        if (_memberMappingByType.TryGetValue(type, out var memberMapping))
        {
            var lookup = memberMapping.GetAlternateLookup<ReadOnlySpan<char>>();
            if (lookup.TryGetValue(engineMemberName, out fullyQualifiedMemberName))
            {
                return true;
            }
        }

        if (_lazyMemberMappingByType.TryGetValue(type, out var lazyMemberMapping))
        {
            var lookup = lazyMemberMapping.GetAlternateLookup<ReadOnlySpan<char>>();
            if (lookup.TryGetValue(engineMemberName, out var lazyFullyQualifiedMemberName))
            {
                fullyQualifiedMemberName = lazyFullyQualifiedMemberName();

                // If we found it, register it in the regular mapping so we don't have to call the lazy function again.
                RegisterMemberMapping(type, engineMemberName.ToString(), fullyQualifiedMemberName);

                return true;
            }
        }

        if (type.BaseType is not null)
        {
            return TryGetMemberMappingCore(type.BaseType, engineMemberName, out fullyQualifiedMemberName);
        }

        fullyQualifiedMemberName = null;
        return false;
    }

    /// <summary>
    /// Determines if <paramref name="type"/> can be compile-time constant
    /// so it can have a default parameter value.
    /// </summary>
    /// <param name="type">The type to check for.</param>
    /// <returns>Whether the type can be compile-time constant.</returns>
    public static bool CanTypeBeConstant(TypeInfo type)
    {
        // The types that can be constant per the C# language reference
        // https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/language-specification/expressions#1223-constant-expressions
        return type == KnownTypes.SystemSByte
            || type == KnownTypes.SystemByte
            || type == KnownTypes.SystemInt16
            || type == KnownTypes.SystemUInt16
            || type == KnownTypes.SystemInt32
            || type == KnownTypes.SystemUInt32
            || type == KnownTypes.SystemInt64
            || type == KnownTypes.SystemUInt64
            || type == KnownTypes.SystemChar
            || type == KnownTypes.SystemSingle
            || type == KnownTypes.SystemDouble
            || type == KnownTypes.SystemDecimal
            || type == KnownTypes.SystemBoolean
            || type == KnownTypes.SystemString
            || type.IsEnum;
    }

    /// <summary>
    /// Determines if the type with the given engine name is one of the packed
    /// array types.
    /// </summary>
    /// <param name="engineTypeName">The name of the type in the Godot engine.</param>
    /// <returns>Whether the type is a packed array.</returns>
    public static bool IsTypePackedArray(string engineTypeName)
    {
        return engineTypeName is "PackedByteArray"
                              or "PackedInt32Array"
                              or "PackedInt64Array"
                              or "PackedFloat32Array"
                              or "PackedFloat64Array"
                              or "PackedStringArray"
                              or "PackedVector2Array"
                              or "PackedVector3Array"
                              or "PackedVector4Array"
                              or "PackedColorArray";
    }
}
