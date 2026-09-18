using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace ConfioGenerator;

/// <summary>
/// 从手写 JSON 上下文生成配置声明，原生 JSON 生成器独立处理同一份输入。
/// </summary>
[Generator(LanguageNames.CSharp)]
public sealed class SettingsGenerator : IIncrementalGenerator
{
    private const string SectionAttribute = "Confio.SettingsSectionAttribute";
    private const string ProtectionAttribute = "Confio.ProtectedAttribute";
    private const string JsonIgnoreAttribute = "System.Text.Json.Serialization.JsonIgnoreAttribute";
    private const string JsonNameAttribute = "System.Text.Json.Serialization.JsonPropertyNameAttribute";

    private static readonly DiagnosticDescriptor InvalidContext = Rule(
        "CONFIO001", "Invalid configuration context",
        "Configuration context '{0}' and its containing types must be partial, non-generic classes or structs");
    private static readonly DiagnosticDescriptor InvalidModel = Rule(
        "CONFIO002", "Invalid configuration model",
        "Configuration model '{0}' must be a concrete class that can be constructed without arguments or required member assignments");
    private static readonly DiagnosticDescriptor Conflict = Rule(
        "CONFIO003", "Conflicting configuration sections",
        "Configuration section '{0}' conflicts with another declaration or has an invalid path");
    private static readonly DiagnosticDescriptor InvalidMember = Rule(
        "CONFIO004", "Invalid persisted member",
        "Protected member '{0}' must be public, non-static, and serialized");
    private static readonly DiagnosticDescriptor InvalidOptions = Rule(
        "CONFIO005", "Unsupported JSON context options",
        "Context '{0}' must retain default-valued members, use replacement semantics, and provide serialization metadata");

    /// <inheritdoc />
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var contexts = context.SyntaxProvider.ForAttributeWithMetadataName(
            "System.Text.Json.Serialization.JsonSerializableAttribute",
            static (node, _) => node is ClassDeclarationSyntax,
            static (syntax, _) => (Type: (INamedTypeSymbol)syntax.TargetSymbol, Attributes: syntax.Attributes));

        context.RegisterSourceOutput(contexts, static (output, entry) => Generate(output, entry.Type, entry.Attributes));
    }

    private static void Generate(SourceProductionContext output, INamedTypeSymbol context, ImmutableArray<AttributeData> attributes)
    {
        var models = attributes
            .Select(a => a.ConstructorArguments.FirstOrDefault().Value as INamedTypeSymbol)
            .Where(t => t is not null && Attribute(t, SectionAttribute) is not null)
            .Cast<INamedTypeSymbol>()
            .ToArray();
        if (models.Length == 0)
        {
            return;
        }

        var containers = new Stack<INamedTypeSymbol>();
        for (var type = context; type is not null; type = type.ContainingType)
        {
            if (type.Arity != 0 || type.DeclaringSyntaxReferences.Any(r =>
                    r.GetSyntax(output.CancellationToken) is not TypeDeclarationSyntax declaration ||
                    !declaration.Modifiers.Any(SyntaxKind.PartialKeyword)))
            {
                output.ReportDiagnostic(Diagnostic.Create(InvalidContext, context.Locations.FirstOrDefault(), context.Name));
                return;
            }
            containers.Push(type);
        }

        var contextOptions = Attribute(context, "System.Text.Json.Serialization.JsonSourceGenerationOptionsAttribute");
        if (contextOptions is not null && contextOptions.NamedArguments.Any(pair =>
                (pair.Key == "DefaultIgnoreCondition" && Number(pair.Value) != 0) ||
                (pair.Key == "PreferredObjectCreationHandling" && Number(pair.Value) != 0) ||
                (pair.Key == "GenerationMode" && Number(pair.Value) == 2) ||
                (pair.Key == "IgnoreReadOnlyProperties" && pair.Value.Value is true) ||
                (pair.Key == "IgnoreReadOnlyFields" && pair.Value.Value is true)))
        {
            output.ReportDiagnostic(Diagnostic.Create(InvalidOptions, context.Locations.FirstOrDefault(), context.Name));
            return;
        }

        var sections = new List<string>();
        var registrations = new StringBuilder();
        var defaultTypes = new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default);
        foreach (var model in models)
        {
            output.CancellationToken.ThrowIfCancellationRequested();
            if (model.TypeKind != TypeKind.Class || !CanCreateDefault(model))
            {
                output.ReportDiagnostic(Diagnostic.Create(InvalidModel, model.Locations.FirstOrDefault(), model.Name));
                return;
            }

            var section = Attribute(model, SectionAttribute)!.ConstructorArguments[0].Value as string ?? "";
            if (section.Length != 0 && section.Split(':').Any(p => string.IsNullOrWhiteSpace(p) ||
                    p != p.Trim()) ||
                sections.Any(s => s.Length == 0 || section.Length == 0 || string.Equals(s, section, StringComparison.Ordinal) ||
                    s.StartsWith(section + ":", StringComparison.Ordinal) ||
                    section.StartsWith(s + ":", StringComparison.Ordinal)))
            {
                output.ReportDiagnostic(Diagnostic.Create(Conflict, model.Locations.FirstOrDefault(), section));
                return;
            }
            sections.Add(section);

            var members = new Dictionary<string, ISymbol>(StringComparer.Ordinal);
            var parents = new Dictionary<ITypeSymbol, HashSet<ITypeSymbol>>(SymbolEqualityComparer.Default);
            var includeFields = contextOptions?.NamedArguments.Any(p => p.Key == "IncludeFields" && p.Value.Value is true) == true;
            if (!CollectMembers(output, model, new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default), members, parents, includeFields))
            {
                return;
            }
            CollectDefaultTypes(model, defaultTypes, includeFields);

            registrations.Append("            global::Confio.SettingsDeclaration.Create<")
                .Append(model.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat))
                .Append(">(this, ").Append(SymbolDisplay.FormatLiteral(section, true))
                .Append(", CreateDefault");
            foreach (var member in members.OrderBy(m => m.Key, StringComparer.Ordinal).Select(m => m.Value))
            {
                var explicitName = Attribute(member, JsonNameAttribute)?.ConstructorArguments[0].Value as string;
                registrations.Append(",\n                new global::Confio.ProtectedMember(typeof(")
                    .Append(member.ContainingType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat))
                    .Append("), ").Append(SymbolDisplay.FormatLiteral(member.Name, true));
                registrations.Append(", ").Append(explicitName is null ? "null" : SymbolDisplay.FormatLiteral(explicitName, true));
                foreach (var container in Containers(member.ContainingType, parents))
                {
                    registrations.Append(", typeof(")
                        .Append(container.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)).Append(")");
                }
                registrations.Append(")");
            }
            registrations.Append("),\n");
        }

        var source = new StringBuilder("// <auto-generated/>\n#nullable enable\n");
        var hasNamespace = !context.ContainingNamespace.IsGlobalNamespace;
        if (hasNamespace)
        {
            source.Append("namespace ").Append(context.ContainingNamespace.ToDisplayString()).Append("\n{\n");
        }
        foreach (var type in containers)
        {
            var kind = type.IsRecord
                ? (type.TypeKind == TypeKind.Struct ? "record struct" : "record class")
                : (type.TypeKind == TypeKind.Struct ? "struct" : "class");
            source.Append("partial ").Append(kind).Append(" @").Append(type.Name);
            if (SymbolEqualityComparer.Default.Equals(type, context))
            {
                source.Append(" : global::Confio.ISettingsContext");
            }
            source.Append("\n{\n");
        }

        source.Append("    /// <inheritdoc />\n")
            .Append("    global::System.Collections.Generic.IReadOnlyList<global::Confio.SettingsDeclaration> ")
            .Append("global::Confio.ISettingsContext.GetSettingsDeclarations()\n    {\n")
            .Append("        static object? CreateDefault(global::System.Type type)\n        {\n");
        foreach (var type in defaultTypes.OfType<INamedTypeSymbol>()
                     .Where(type => CanCreateDefault(type) &&
                         (models.Contains(type, SymbolEqualityComparer.Default) || Hierarchy(type).SelectMany(t => t.GetMembers())
                             .OfType<IPropertySymbol>().Any(property => property.SetMethod?.IsInitOnly == true)))
                     .OrderBy(type => type.ToDisplayString(), StringComparer.Ordinal))
        {
            var name = type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            source.Append("            if (type == typeof(").Append(name).Append(")) return new ")
                .Append(name).Append("();\n");
        }
        source.Append("            return null;\n        }\n\n")
            .Append("        return new global::Confio.SettingsDeclaration[]\n        {\n")
            .Append(registrations)
            .Append("        };\n    }\n");
        foreach (var _ in containers)
        {
            source.Append("}\n");
        }
        if (hasNamespace)
        {
            source.Append("}\n");
        }

        using var hash = SHA256.Create();
        var suffix = BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(context.ToDisplayString())))
            .Replace("-", "").Substring(0, 16);
        output.AddSource(context.Name + "." + suffix + ".Confio.g.cs", SourceText.From(source.ToString(), Encoding.UTF8));
    }

    private static bool CanCreateDefault(INamedTypeSymbol type)
    {
        if (type.IsAbstract || type.IsRefLikeType || type.TypeKind is not (TypeKind.Class or TypeKind.Struct))
        {
            return false;
        }
        var constructor = type.InstanceConstructors
            .Where(c => c.DeclaredAccessibility == Accessibility.Public && c.Parameters.All(p => p.IsOptional))
            .OrderBy(c => c.Parameters.Length).FirstOrDefault();
        return constructor is not null &&
            (Attribute(constructor, "System.Diagnostics.CodeAnalysis.SetsRequiredMembersAttribute") is not null ||
             !Hierarchy(type).SelectMany(t => t.GetMembers()).Any(member =>
                 member is IPropertySymbol { IsRequired: true } or IFieldSymbol { IsRequired: true }));
    }

    private static IEnumerable<INamedTypeSymbol> Hierarchy(INamedTypeSymbol type)
    {
        for (var current = type; current is not null && current.SpecialType != SpecialType.System_Object; current = current.BaseType)
        {
            yield return current;
        }
    }

    private static void CollectDefaultTypes(ITypeSymbol type, HashSet<ITypeSymbol> visited, bool includeFields)
    {
        if (!visited.Add(type)) return;
        if (type is IArrayTypeSymbol array)
        {
            CollectDefaultTypes(array.ElementType, visited, includeFields);
            return;
        }
        if (type is not INamedTypeSymbol named) return;
        if (named.SpecialType != SpecialType.None)
        {
            foreach (var argument in named.TypeArguments) CollectDefaultTypes(argument, visited, includeFields);
            return;
        }
        var enumerable = named.AllInterfaces.FirstOrDefault(i =>
            i.OriginalDefinition.SpecialType == SpecialType.System_Collections_Generic_IEnumerable_T);
        if (enumerable is not null)
        {
            CollectDefaultTypes(enumerable.TypeArguments[0], visited, includeFields);
            return;
        }
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var member in Hierarchy(named).SelectMany(t => t.GetMembers()))
        {
            var memberType = SerializableMemberType(member, includeFields);
            if (memberType is null || !names.Add(member.Name) || IgnoreCondition(member) == 1) continue;
            CollectDefaultTypes(memberType, visited, includeFields);
        }
    }

    private static ITypeSymbol? SerializableMemberType(ISymbol member, bool includeFields) =>
        member.IsStatic || member.DeclaredAccessibility != Accessibility.Public ? null : member switch
        {
            IPropertySymbol { IsIndexer: false } property => property.Type,
            IFieldSymbol { IsImplicitlyDeclared: false } field when includeFields ||
                Attribute(field, "System.Text.Json.Serialization.JsonIncludeAttribute") is not null => field.Type,
            _ => null
        };

    private static int IgnoreCondition(ISymbol member)
    {
        var ignored = Attribute(member, JsonIgnoreAttribute);
        if (ignored is null) return 0;
        var condition = ignored.NamedArguments.FirstOrDefault(p => p.Key == "Condition").Value;
        return condition.Value is null ? 1 : Number(condition);
    }

    private static bool CollectMembers(SourceProductionContext output, ITypeSymbol type,
        HashSet<ITypeSymbol> visited, Dictionary<string, ISymbol> protectedMembers,
        Dictionary<ITypeSymbol, HashSet<ITypeSymbol>> parents, bool includeFields, ITypeSymbol? parent = null)
    {
        output.CancellationToken.ThrowIfCancellationRequested();
        if (parent is not null)
        {
            AddParent(type, parent, parents);
        }
        if (!visited.Add(type))
        {
            return true;
        }
        if (type is IArrayTypeSymbol array)
        {
            return CollectMembers(output, array.ElementType, visited, protectedMembers, parents, includeFields, type);
        }
        if (type is not INamedTypeSymbol named)
        {
            return true;
        }
        if (named.SpecialType != SpecialType.None)
        {
            foreach (var argument in named.TypeArguments)
            {
                if (!CollectMembers(output, argument, visited, protectedMembers, parents, includeFields, type))
                {
                    return false;
                }
            }
            return true;
        }
        var enumerable = named.AllInterfaces.FirstOrDefault(i =>
            i.OriginalDefinition.SpecialType == SpecialType.System_Collections_Generic_IEnumerable_T);
        if (enumerable is not null)
        {
            return CollectMembers(output, enumerable.TypeArguments[0], visited, protectedMembers, parents, includeFields, type);
        }

        var names = new HashSet<string>(StringComparer.Ordinal);
        for (var declaring = named; declaring is not null && declaring.SpecialType != SpecialType.System_Object; declaring = declaring.BaseType)
        {
            foreach (var member in declaring.GetMembers())
            {
                var protection = Attribute(member, ProtectionAttribute);
                for (var overridden = (member as IPropertySymbol)?.OverriddenProperty;
                     protection is null && overridden is not null; overridden = overridden.OverriddenProperty)
                {
                    protection = Attribute(overridden, ProtectionAttribute);
                }
                var memberType = SerializableMemberType(member, includeFields);
                var ignoreCondition = IgnoreCondition(member);
                if (protection is not null && (ignoreCondition == 1 || memberType is null))
                {
                    output.ReportDiagnostic(Diagnostic.Create(InvalidMember, member.Locations.FirstOrDefault(), member.Name));
                    return false;
                }
                // 不参与持久化的同名成员不能遮蔽基类的公开属性；无效保护声明仍在上方诊断。
                if (memberType is null || !names.Add(member.Name) || ignoreCondition == 1)
                {
                    continue;
                }
                if (protection is not null)
                {
                    protectedMembers[member.ContainingType.ToDisplayString() + "." + member.Name] = member;
                    AddParent(member.ContainingType, type, parents);
                }
                else if (!CollectMembers(output, memberType, visited, protectedMembers, parents, includeFields, type))
                {
                    return false;
                }
            }
        }
        return true;
    }

    private static void AddParent(ITypeSymbol type, ITypeSymbol parent,
        Dictionary<ITypeSymbol, HashSet<ITypeSymbol>> parents)
    {
        if (!parents.TryGetValue(type, out var values))
        {
            values = new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default);
            parents.Add(type, values);
        }
        values.Add(parent);
    }

    private static IEnumerable<ITypeSymbol> Containers(ITypeSymbol owner,
        Dictionary<ITypeSymbol, HashSet<ITypeSymbol>> parents)
    {
        var found = new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default) { owner };
        var pending = new Stack<ITypeSymbol>();
        pending.Push(owner);
        while (pending.Count != 0)
        {
            if (parents.TryGetValue(pending.Pop(), out var values))
            {
                foreach (var value in values)
                {
                    if (found.Add(value))
                    {
                        pending.Push(value);
                    }
                }
            }
        }
        return found.OrderBy(type => type.ToDisplayString(), StringComparer.Ordinal);
    }

    private static AttributeData? Attribute(ISymbol symbol, string name) =>
        symbol.GetAttributes().FirstOrDefault(a => a.AttributeClass?.ToDisplayString() == name);

    private static int Number(TypedConstant value) => Convert.ToInt32(value.Value, CultureInfo.InvariantCulture);

    private static DiagnosticDescriptor Rule(string id, string title, string message) =>
        new(id, title, message, "Confio", DiagnosticSeverity.Error, isEnabledByDefault: true);
}
