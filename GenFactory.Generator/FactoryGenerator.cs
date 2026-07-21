using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace GenFactory.Generator
{
    [Generator]
    public sealed class FactoryGenerator : IIncrementalGenerator
    {
        private const string GenerateFactoryAttribute = "GenFactory.GenerateFactoryAttribute";
        private const string FactoryArgAttribute = "GenFactory.FactoryArgAttribute";
        private const string FactoryCtorAttribute = "GenFactory.FactoryCtorAttribute";
        private const string DefaultRegistryNamespace = "GenFactory.Generated";

        // Every emitted type reference is fully global::-qualified, including built-in types
        // (global::System.Int32 rather than the "int" keyword). UseSpecialTypes is deliberately NOT
        // set: a keyword like "int" or "string" can be shadowed by a type of that name in the
        // consumer's scope, whereas the global::-qualified form never can.
        private static readonly SymbolDisplayFormat Fqn =
            SymbolDisplayFormat.FullyQualifiedFormat.WithMiscellaneousOptions(
                SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier);

        private static readonly SymbolDisplayFormat FqnNoNull =
            SymbolDisplayFormat.FullyQualifiedFormat;

        public void Initialize(IncrementalGeneratorInitializationContext context)
        {
            var models = context.SyntaxProvider.ForAttributeWithMetadataName(
                    GenerateFactoryAttribute,
                    static (node, _) => node is ClassDeclarationSyntax,
                    static (ctx, token) => Transform(ctx, token))
                .Where(static m => m is not null)
                .Select(static (m, _) => m!);

            context.RegisterSourceOutput(models, static (spc, model) => EmitFactory(spc, model));

            // Which DI providers are referenced (bitmask) plus the per-assembly registry namespace.
            // Both are cache-equatable, so the registry only re-emits when one of them changes.
            var registryInfo = context.CompilationProvider.Select(static (c, _) =>
            {
                int mask = 0;
                for (int i = 0; i < DiProvider.All.Length; i++)
                {
                    if (c.GetTypeByMetadataName(DiProvider.All[i].MarkerType) is not null)
                        mask |= 1 << i;
                }

                return (Mask: mask, Namespace: MakeRegistryNamespace(c.AssemblyName));
            });

            context.RegisterSourceOutput(models.Collect().Combine(registryInfo),
                static (spc, pair) => EmitRegistry(spc, pair.Left, pair.Right.Mask, pair.Right.Namespace));
        }

        private static FactoryModel? Transform(GeneratorAttributeSyntaxContext ctx, CancellationToken token)
        {
            var type = (INamedTypeSymbol)ctx.TargetSymbol;
            AttributeData attribute = ctx.Attributes[0];
            Location location = ctx.TargetNode.GetLocation();

            string? factoryNameOverride = GetNamedString(attribute, "FactoryName");
            var returnTypeOverride = GetNamedArg(attribute, "ReturnType").Value as ITypeSymbol;

            string className = type.Name;
            string factoryName = factoryNameOverride ?? className + "Factory";
            string interfaceName = "I" + factoryName;
            string ns = type.ContainingNamespace.IsGlobalNamespace
                ? string.Empty
                : type.ContainingNamespace.ToDisplayString();

            // Classes nested in a generic type aren't supported: the outer's type parameters would
            // need to be hoisted onto the factory (a real feature of its own), and without that the
            // generated namespace-scope code would reference an out-of-scope type parameter (CS0246).
            for (INamedTypeSymbol? outer = type.ContainingType; outer is not null; outer = outer.ContainingType)
            {
                if (outer.TypeParameters.Length > 0)
                    return Error(ns, className, factoryName, interfaceName, string.Empty,
                        $"[GenerateFactory] on '{type.Name}': classes nested in a generic type are not supported.",
                        location);
            }

            // A factory emitted at namespace scope can only reach a target that's public/internal all
            // the way up its containing-type chain; private/protected/private-protected nesting would
            // compile to an inaccessible reference (CS0122) regardless of what accessibility we pick.
            bool allPublic = true;
            for (INamedTypeSymbol? t = type; t is not null; t = t.ContainingType)
            {
                switch (t.DeclaredAccessibility)
                {
                    case Accessibility.Public:
                        break;
                    case Accessibility.Internal:
                    case Accessibility.ProtectedOrInternal:
                        allPublic = false;
                        break;
                    default:
                        return Error(ns, className, factoryName, interfaceName, string.Empty,
                            $"[GenerateFactory] on '{type.Name}': '{t.Name}' is {t.DeclaredAccessibility}; a factory emitted at namespace scope can't reach it.",
                            location);
                }
            }

            string returnType = returnTypeOverride is not null
                ? returnTypeOverride.ToDisplayString(Fqn)
                : ResolveReturnType(type, className);

            IMethodSymbol? ctor = SelectConstructor(type, out string? ctorError);
            if (ctor is null)
                return Error(ns, className, factoryName, interfaceName, returnType, ctorError!, location);

            var ctorParams = new List<CtorParam>(ctor.Parameters.Length);
            var injectedFieldNames = new HashSet<string>();
            var createParamNames = new HashSet<string>();
            foreach (IParameterSymbol p in ctor.Parameters)
            {
                token.ThrowIfCancellationRequested();

                if (p.RefKind is RefKind.Ref or RefKind.Out)
                    return Error(ns, className, factoryName, interfaceName, returnType,
                        $"[GenerateFactory] on '{className}': parameter '{p.Name}' uses '{(p.RefKind == RefKind.Ref ? "ref" : "out")}', which isn't supported.",
                        location);

                ParamRole role;
                if (HasAttribute(p, FactoryArgAttribute))
                {
                    role = ParamRole.Arg;
                    if (!createParamNames.Add(p.Name))
                        return Error(ns, className, factoryName, interfaceName, returnType,
                            $"[GenerateFactory] on '{className}': multiple Create parameters are named '{p.Name}'; rename one of them.",
                            location);
                }
                else
                {
                    role = ParamRole.Inject;
                    string field = Field(p.Name);
                    if (!injectedFieldNames.Add(field))
                        return Error(ns, className, factoryName, interfaceName, returnType,
                            $"[GenerateFactory] on '{className}': multiple constructor parameters map to the same field '{field}'; rename one of them.",
                            location);
                }

                ctorParams.Add(new CtorParam(
                    ParamTypeText(p),
                    p.Name,
                    role,
                    p.HasExplicitDefaultValue,
                    p.HasExplicitDefaultValue ? RenderDefault(p) : null,
                    p.RefKind == RefKind.In,
                    p.IsParams));
            }

            var propArgs = new List<PropArg>();
            foreach (ISymbol member in type.GetMembers())
            {
                if (member is not IPropertySymbol prop || !HasAttribute(prop, FactoryArgAttribute))
                    continue;

                if (prop.SetMethod is null)
                    return Error(ns, className, factoryName, interfaceName, returnType,
                        $"[FactoryArg] on '{className}.{prop.Name}': property must be settable or init-only.",
                        location);

                string paramName = Camel(prop.Name);
                if (!createParamNames.Add(paramName))
                    return Error(ns, className, factoryName, interfaceName, returnType,
                        $"[GenerateFactory] on '{className}': multiple Create parameters are named '{paramName}'; rename one of them.",
                        location);

                propArgs.Add(new PropArg(PropTypeText(prop), prop.Name, paramName));
            }

            string accessibility = allPublic ? "public" : "internal";
            (string typeParams, string constraints) = BuildGenerics(type);

            return new FactoryModel(ns, type.ToDisplayString(Fqn), factoryName, interfaceName, returnType,
                EquatableArray<CtorParam>.From(ctorParams), EquatableArray<PropArg>.From(propArgs),
                null, Location.None, accessibility, typeParams, constraints, type.TypeParameters.Length,
                CollectUsings(type));
        }

        /// <summary>Renders a generic type's own type-parameter list ("&lt;T&gt;") and its "where" constraint clauses.</summary>
        private static (string TypeParams, string Constraints) BuildGenerics(INamedTypeSymbol type)
        {
            if (type.TypeParameters.Length == 0)
                return (string.Empty, string.Empty);

            string typeParams = "<" + string.Join(", ", type.TypeParameters.Select(tp => tp.Name)) + ">";

            var clauses = new List<string>();
            foreach (ITypeParameterSymbol tp in type.TypeParameters)
            {
                var parts = new List<string>();
                if (tp.HasUnmanagedTypeConstraint)
                    parts.Add("unmanaged");
                else if (tp.HasValueTypeConstraint)
                    parts.Add("struct");
                else if (tp.HasReferenceTypeConstraint)
                    parts.Add(tp.ReferenceTypeConstraintNullableAnnotation == NullableAnnotation.Annotated ? "class?" : "class");

                if (tp.HasNotNullConstraint)
                    parts.Add("notnull");

                foreach (ITypeSymbol c in tp.ConstraintTypes)
                    parts.Add(c.ToDisplayString(Fqn));

                if (tp.HasConstructorConstraint)
                    parts.Add("new()");

                if (parts.Count > 0)
                    clauses.Add($"where {tp.Name} : {string.Join(", ", parts)}");
            }

            string constraints = clauses.Count == 0 ? string.Empty : " " + string.Join(" ", clauses);
            return (typeParams, constraints);
        }

        private static IMethodSymbol? SelectConstructor(INamedTypeSymbol type, out string? error)
        {
            error = null;
            var ctors = type.InstanceConstructors
                .Where(c => c.DeclaredAccessibility == Accessibility.Public && !c.IsStatic)
                .ToArray();

            if (ctors.Length == 0)
            {
                error = $"[GenerateFactory] on '{type.Name}': no public constructor found.";
                return null;
            }

            if (ctors.Length == 1)
                return ctors[0];

            var marked = ctors.Where(c => HasAttribute(c, FactoryCtorAttribute)).ToArray();
            if (marked.Length == 1)
                return marked[0];

            error = marked.Length == 0
                ? $"[GenerateFactory] on '{type.Name}': multiple public constructors; mark one with [FactoryCtor]."
                : $"[GenerateFactory] on '{type.Name}': multiple constructors marked with [FactoryCtor].";
            return null;
        }

        private static string ResolveReturnType(INamedTypeSymbol type, string className)
        {
            string wanted = "I" + className;

            // Prefer the "I{ClassName}" interface declared in the same namespace as the class; only
            // fall back to a same-named interface elsewhere so an unrelated type with a colliding
            // simple name in another namespace doesn't get picked as the return type.
            INamedTypeSymbol? fallback = null;
            foreach (INamedTypeSymbol iface in type.AllInterfaces)
            {
                if (iface.Name != wanted)
                    continue;
                if (SymbolEqualityComparer.Default.Equals(iface.ContainingNamespace, type.ContainingNamespace))
                    return iface.ToDisplayString(Fqn);
                fallback ??= iface;
            }

            return (fallback ?? type).ToDisplayString(Fqn);
        }

        // ---- Emit -------------------------------------------------------------------------

        private static void EmitFactory(SourceProductionContext spc, FactoryModel m)
        {
            if (m.Error is not null)
            {
                spc.ReportDiagnostic(Diagnostic.Create(ErrorDescriptor, m.Location, m.Error));
                return;
            }

            string createParams = BuildCreateParams(m);
            var sb = new StringBuilder();
            sb.AppendLine("// <auto-generated/>");
            sb.AppendLine("#nullable enable");
            // Emitted at file scope (before any namespace) so they cover error-typed references the
            // same way they do in the source. Harmless for global::-qualified types; an unused using
            // never errors, and CS8019 is off by default and suppressed in generated files anyway.
            if (m.Usings.Count > 0)
            {
                foreach (string u in m.Usings)
                    sb.AppendLine(u);
                sb.AppendLine();
            }

            bool hasNs = m.Namespace.Length > 0;
            string indent = hasNs ? "    " : string.Empty;
            if (hasNs)
            {
                sb.Append("namespace ").Append(m.Namespace).AppendLine();
                sb.AppendLine("{");
            }

            // Interface
            sb.Append(indent).Append(m.Accessibility).Append(" interface ").Append(m.InterfaceName)
                .Append(m.TypeParams).Append(m.Constraints).AppendLine();
            sb.Append(indent).AppendLine("{");
            sb.Append(indent).Append("    ").Append(m.ReturnTypeFqn).Append(" Create(").Append(createParams)
                .AppendLine(");");
            sb.Append(indent).AppendLine("}");
            sb.AppendLine();

            // Implementation
            sb.Append(indent).Append(m.Accessibility).Append(" sealed class ").Append(m.FactoryName)
                .Append(m.TypeParams).Append(" : ").Append(m.InterfaceName).Append(m.TypeParams)
                .Append(m.Constraints).AppendLine();
            sb.Append(indent).AppendLine("{");

            var injected = m.CtorParams.Where(p => p.Role == ParamRole.Inject).ToArray();
            foreach (CtorParam p in injected)
                sb.Append(indent).Append("    private readonly ").Append(p.TypeFqn).Append(' ')
                    .Append(Field(p.Name)).AppendLine(";");
            if (injected.Length > 0)
                sb.AppendLine();

            sb.Append(indent).Append("    public ").Append(m.FactoryName).Append('(')
                .Append(string.Join(", ", injected.Select(DeclareParam))).AppendLine(")");
            sb.Append(indent).AppendLine("    {");
            foreach (CtorParam p in injected)
                sb.Append(indent).Append("        ").Append(Field(p.Name)).Append(" = ").Append(p.Name)
                    .AppendLine(";");
            sb.Append(indent).AppendLine("    }");
            sb.AppendLine();

            sb.Append(indent).Append("    public ").Append(m.ReturnTypeFqn).Append(" Create(").Append(createParams)
                .AppendLine(")");
            sb.Append(indent).AppendLine("    {");
            string args = string.Join(", ", m.CtorParams.Select(RenderCtorArg));
            string objInit = m.PropArgs.Count == 0
                ? string.Empty
                : " { " + string.Join(", ", m.PropArgs.Select(a => a.PropName + " = " + a.ParamName)) + " }";
            sb.Append(indent).Append("        return new ").Append(m.ClassFqn).Append('(').Append(args)
                .Append(')').Append(objInit).AppendLine(";");
            sb.Append(indent).AppendLine("    }");

            sb.Append(indent).AppendLine("}");
            if (hasNs)
                sb.AppendLine("}");

            // Qualify the hint name with the namespace: two [GenerateFactory] classes that share a
            // simple name in different namespaces would otherwise collide and abort generation.
            string hintName = (m.Namespace.Length == 0 ? m.FactoryName : m.Namespace + "." + m.FactoryName) + ".g.cs";
            spc.AddSource(hintName, sb.ToString());
        }

        private static string BuildCreateParams(FactoryModel m)
        {
            // (rendered declaration, is this a "params" ctor parameter). "params" can only be kept if
            // this entry ends up last after required ctor args, then property args, then optional args
            // are concatenated below — a params ctor parameter is always last among the required ctor
            // args (C# guarantees it's the constructor's last parameter), but property args or optional
            // args appended after it would bump it out of last place, so it silently degrades to a
            // plain array parameter in that case (still callable via an explicit array).
            var required = new List<(string Text, bool IsParams)>();
            var optional = new List<(string Text, bool IsParams)>();

            foreach (CtorParam p in m.CtorParams)
            {
                if (p.Role != ParamRole.Arg)
                    continue;
                string modifier = p.IsIn ? "in " : string.Empty;
                if (p.IsOptional)
                    optional.Add(($"{modifier}{p.TypeFqn} {p.Name} = {p.DefaultLiteral}", false));
                else
                    required.Add(($"{modifier}{p.TypeFqn} {p.Name}", p.IsParams));
            }

            foreach (PropArg a in m.PropArgs)
                required.Add(($"{a.TypeFqn} {a.ParamName}", false));

            var all = required.Concat(optional).ToList();
            if (all.Count > 0 && all[all.Count - 1].IsParams)
                all[all.Count - 1] = ("params " + all[all.Count - 1].Text, false);

            return string.Join(", ", all.Select(x => x.Text));
        }

        private static string DeclareParam(CtorParam p) => (p.IsIn ? "in " : string.Empty) + p.TypeFqn + " " + p.Name;

        private static string RenderCtorArg(CtorParam p) => p.Role switch
        {
            ParamRole.Inject => Field(p.Name),
            _ => p.Name, // Arg is forwarded from the Create parameters as-is
        };

        private static void EmitRegistry(SourceProductionContext spc, ImmutableArray<FactoryModel> models,
            int providerMask, string registryNamespace)
        {
            var valid = models.Where(m => m.Error is null).ToArray();
            if (valid.Length == 0)
                return;

            var present = new List<DiProvider>();
            for (int i = 0; i < DiProvider.All.Length; i++)
            {
                if ((providerMask & (1 << i)) != 0)
                    present.Add(DiProvider.All[i]);
            }

            var sb = new StringBuilder();
            sb.AppendLine("// <auto-generated/>");
            sb.AppendLine("#nullable enable");
            sb.Append("namespace ").Append(registryNamespace).AppendLine();
            sb.AppendLine("{");
            foreach (string? u in present.Select(p => p.Using).Where(u => u is not null).Distinct())
                sb.Append("    using ").Append(u).AppendLine(";");
            sb.AppendLine("    /// <summary>A generated factory's interface/implementation pair and its namespace.</summary>");
            sb.AppendLine("    public readonly struct FactoryRegistration");
            sb.AppendLine("    {");
            sb.AppendLine("        public readonly global::System.Type InterfaceType;");
            sb.AppendLine("        public readonly global::System.Type ImplementationType;");
            sb.AppendLine("        public readonly string Namespace;");
            sb.AppendLine();
            sb.AppendLine("        public FactoryRegistration(global::System.Type interfaceType, global::System.Type implementationType, string ns)");
            sb.AppendLine("        {");
            sb.AppendLine("            InterfaceType = interfaceType;");
            sb.AppendLine("            ImplementationType = implementationType;");
            sb.AppendLine("            Namespace = ns;");
            sb.AppendLine("        }");
            sb.AppendLine("    }");
            sb.AppendLine();
            sb.AppendLine("    /// <summary>DI-agnostic list of every generated factory in this assembly.</summary>");
            sb.AppendLine("    public static class GeneratedFactoryRegistry");
            sb.AppendLine("    {");
            sb.AppendLine("        public static readonly FactoryRegistration[] Registrations = new FactoryRegistration[]");
            sb.AppendLine("        {");
            foreach (FactoryModel m in valid.OrderBy(m => m.Namespace).ThenBy(m => m.FactoryName))
            {
                string ifaceFqn = Qualify(m.Namespace, m.InterfaceName, m.Arity);
                string implFqn = Qualify(m.Namespace, m.FactoryName, m.Arity);
                sb.Append("            new FactoryRegistration(typeof(").Append(ifaceFqn).Append("), typeof(")
                    .Append(implFqn).Append("), \"").Append(m.Namespace).AppendLine("\"),");
            }

            sb.AppendLine("        };");
            sb.AppendLine("    }");

            foreach (DiProvider p in present)
                AppendBridge(sb, p);

            sb.AppendLine("}");
            spc.AddSource("GeneratedFactoryRegistry.g.cs", sb.ToString());
        }

        /// <summary>Emits a <c>RegisterGeneratedFactories</c> extension for one DI container.</summary>
        private static void AppendBridge(StringBuilder sb, DiProvider p)
        {
            bool hasLifetime = p.LifetimeType is not null;
            string lifetimeParam = hasLifetime ? $", {p.LifetimeType} lifetime = {p.LifetimeDefault}" : string.Empty;
            string lifetimeExpr = hasLifetime ? "lifetime" : string.Empty;
            string registerLine = p.RegisterLine("r.ImplementationType", "r.InterfaceType", lifetimeExpr);

            sb.AppendLine();
            sb.Append("    /// <summary>").Append(p.Name)
                .AppendLine(" bridge over <see cref=\"GeneratedFactoryRegistry\"/>.</summary>");
            sb.Append("    public static class GeneratedFactoryRegistry").Append(Sanitize(p.Name))
                .AppendLine("Extensions");
            sb.AppendLine("    {");
            sb.AppendLine("        /// <summary>Registers generated factories in <paramref name=\"namespacePrefix\"/> or a sub-namespace (null registers all).</summary>");
            sb.Append("        public static ").Append(p.BuilderType)
                .Append(" RegisterGeneratedFactories(this ").Append(p.BuilderType)
                .Append(" builder, string? namespacePrefix = null").Append(lifetimeParam).AppendLine(")");
            sb.AppendLine("        {");
            sb.AppendLine("            var seen = new global::System.Collections.Generic.HashSet<global::System.Type>();");
            sb.AppendLine("            foreach (var r in GeneratedFactoryRegistry.Registrations)");
            sb.AppendLine("            {");
            sb.AppendLine("                if (namespacePrefix != null &&");
            sb.AppendLine("                    r.Namespace != namespacePrefix &&");
            sb.AppendLine("                    !r.Namespace.StartsWith(namespacePrefix + \".\", global::System.StringComparison.Ordinal))");
            sb.AppendLine("                    continue;");
            sb.AppendLine("                if (!seen.Add(r.ImplementationType))");
            sb.AppendLine("                    continue;");
            sb.Append("                ").AppendLine(registerLine);
            sb.AppendLine("            }");
            sb.AppendLine();
            sb.AppendLine("            return builder;");
            sb.AppendLine("        }");
            sb.AppendLine("    }");
        }

        // ---- Helpers ----------------------------------------------------------------------

        private static readonly DiagnosticDescriptor ErrorDescriptor = new(
            "NFG001", "Factory generation error", "{0}", "FactoryGenerator", DiagnosticSeverity.Error, true);

        private static FactoryModel Error(string ns, string className, string factoryName, string interfaceName,
            string returnType, string error, Location location) =>
            new(ns, className, factoryName, interfaceName, returnType,
                EquatableArray<CtorParam>.From(System.Array.Empty<CtorParam>()),
                EquatableArray<PropArg>.From(System.Array.Empty<PropArg>()), error, location);

        /// <summary>
        /// Collects the non-global <c>using</c> directives in scope at every partial declaration of
        /// the target type. These are copied into the generated factory so a parameter/property type
        /// authored in another source generator's output — which this generator sees only as an
        /// unqualifiable <see cref="IErrorTypeSymbol"/> — still binds in the final compilation. Global
        /// usings are skipped: they already apply assembly-wide, including to generated files.
        /// </summary>
        private static EquatableArray<string> CollectUsings(INamedTypeSymbol type)
        {
            var set = new SortedSet<string>(System.StringComparer.Ordinal);
            foreach (SyntaxReference reference in type.DeclaringSyntaxReferences)
            {
                for (SyntaxNode? node = reference.GetSyntax(); node is not null; node = node.Parent)
                {
                    SyntaxList<UsingDirectiveSyntax> usings = node switch
                    {
                        BaseNamespaceDeclarationSyntax ns => ns.Usings,
                        CompilationUnitSyntax cu => cu.Usings,
                        _ => default,
                    };

                    foreach (UsingDirectiveSyntax u in usings)
                    {
                        if (u.GlobalKeyword.IsKind(SyntaxKind.GlobalKeyword))
                            continue;
                        set.Add(u.NormalizeWhitespace().ToString());
                    }
                }
            }

            return EquatableArray<string>.From(set);
        }

        private static string ParamTypeText(IParameterSymbol p) => p.Type.ToDisplayString(Fqn);

        private static string PropTypeText(IPropertySymbol prop) => prop.Type.ToDisplayString(Fqn);

        private static bool HasAttribute(ISymbol symbol, string metadataName) =>
            symbol.GetAttributes().Any(a => a.AttributeClass?.ToDisplayString() == metadataName);

        private static TypedConstant GetNamedArg(AttributeData attribute, string name) =>
            attribute.NamedArguments.FirstOrDefault(kv => kv.Key == name).Value;

        private static string? GetNamedString(AttributeData attribute, string name)
        {
            TypedConstant value = GetNamedArg(attribute, name);
            return value.Value as string;
        }

        private static string RenderDefault(IParameterSymbol p)
        {
            object? value = p.ExplicitDefaultValue;

            // Enums (including Nullable<TEnum>): the default value is boxed as its underlying integer,
            // so cast it back to the enum type; rendering "default" would silently collapse any
            // non-zero member to zero.
            ITypeSymbol underlyingType = p.Type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullable
                ? nullable.TypeArguments[0]
                : p.Type;
            if (underlyingType.TypeKind == TypeKind.Enum)
            {
                if (value is null)
                    return "default";
                string enumFqn = underlyingType.ToDisplayString(FqnNoNull);
                string raw = System.Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? "0";
                return "(" + enumFqn + ")(" + raw + ")";
            }

            if (value is null)
                return "null";
            return value switch
            {
                bool b => b ? "true" : "false",
                string s => SymbolDisplay.FormatLiteral(s, true),
                char c => SymbolDisplay.FormatLiteral(c, true),
                float f => FormatFloat(f),
                double d => FormatDouble(d),
                decimal m => m.ToString(System.Globalization.CultureInfo.InvariantCulture) + "m",
                // Integral types render as plain digits; the compiler infers the literal's type
                // (e.g. a value beyond int range becomes a long/ulong literal) to match the parameter.
                _ => System.Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? "default",
            };
        }

        private static string FormatFloat(float f)
        {
            if (float.IsNaN(f)) return "float.NaN";
            if (float.IsPositiveInfinity(f)) return "float.PositiveInfinity";
            if (float.IsNegativeInfinity(f)) return "float.NegativeInfinity";
            return f.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + "f";
        }

        private static string FormatDouble(double d)
        {
            if (double.IsNaN(d)) return "double.NaN";
            if (double.IsPositiveInfinity(d)) return "double.PositiveInfinity";
            if (double.IsNegativeInfinity(d)) return "double.NegativeInfinity";
            return d.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + "d";
        }

        private static string Field(string name) => "_" + Camel(name);

        private static string Camel(string name) =>
            name.Length == 0 || char.IsLower(name[0]) ? name : char.ToLowerInvariant(name[0]) + name.Substring(1);

        private static string Qualify(string ns, string name, int arity = 0)
        {
            string qualified = ns.Length == 0 ? "global::" + name : "global::" + ns + "." + name;
            // Open-generic typeof syntax: typeof(Foo<>) for arity 1, typeof(Foo<,>) for arity 2, etc.
            return arity > 0 ? qualified + "<" + new string(',', arity - 1) + ">" : qualified;
        }

        /// <summary>
        /// Builds this assembly's registry namespace as <c>{AssemblyName}.Generated</c> so every
        /// generated assembly gets its own registry and bridge extensions instead of all colliding
        /// on a single shared namespace. Each dot-separated segment of the assembly name is coerced
        /// into a valid identifier.
        /// </summary>
        private static string MakeRegistryNamespace(string? assemblyName)
        {
            if (string.IsNullOrEmpty(assemblyName))
                return DefaultRegistryNamespace;

            var sb = new StringBuilder(assemblyName!.Length + 10);
            foreach (string segment in assemblyName.Split('.'))
            {
                if (segment.Length == 0)
                    continue;
                if (sb.Length > 0)
                    sb.Append('.');
                if (!char.IsLetter(segment[0]) && segment[0] != '_')
                    sb.Append('_');
                foreach (char c in segment)
                    sb.Append(char.IsLetterOrDigit(c) ? c : '_');
            }

            if (sb.Length == 0)
                return DefaultRegistryNamespace;

            sb.Append(".Generated");
            return sb.ToString();
        }

        /// <summary>Turns a provider name into a valid identifier fragment for the generated class name.</summary>
        private static string Sanitize(string name)
        {
            var sb = new StringBuilder(name.Length);
            foreach (char c in name)
            {
                if (char.IsLetterOrDigit(c))
                    sb.Append(c);
            }

            return sb.Length == 0 ? "Di" : sb.ToString();
        }
    }
}
