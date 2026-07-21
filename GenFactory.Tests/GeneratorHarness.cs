using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using GenFactory.Generator;

namespace GenFactory.Tests
{
    /// <summary>Runs <see cref="FactoryGenerator"/> over in-memory sources and reports the results.</summary>
    internal static class GeneratorHarness
    {
        private static readonly ImmutableArray<MetadataReference> References = LoadReferences();

        public static Result Run(params string[] sources) => RunCore(null, sources);

        /// <summary>
        /// Runs <see cref="FactoryGenerator"/> alongside a sibling generator that emits
        /// <paramref name="siblingSource"/> via <c>RegisterSourceOutput</c>. That output is invisible
        /// to <see cref="FactoryGenerator"/> (generators can't see each other's output) but present in
        /// the final compilation — exactly the situation that makes a referenced type an error symbol
        /// during generation while still compiling afterwards.
        /// </summary>
        public static Result RunWithSibling(string siblingSource, params string[] sources) =>
            RunCore(siblingSource, sources);

        private static Result RunCore(string? siblingSource, string[] sources)
        {
            var trees = sources.Select(s => CSharpSyntaxTree.ParseText(s)).ToArray();
            var compilation = CSharpCompilation.Create("TestAsm", trees, References,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary,
                    nullableContextOptions: NullableContextOptions.Enable));

            IIncrementalGenerator[] generators = siblingSource is null
                ? new IIncrementalGenerator[] { new FactoryGenerator() }
                : new IIncrementalGenerator[] { new FactoryGenerator(), new SiblingGenerator(siblingSource) };

            var driver = (CSharpGeneratorDriver)CSharpGeneratorDriver
                .Create(generators)
                .RunGeneratorsAndUpdateCompilation(compilation, out Compilation output, out var genDiagnostics);

            string generated = string.Concat(driver.GetRunResult().Results
                .SelectMany(r => r.GeneratedSources)
                .Select(s => s.SourceText.ToString()));

            string[] compileErrors = output.GetDiagnostics()
                .Where(d => d.Severity == DiagnosticSeverity.Error)
                .Select(d => d.ToString())
                .ToArray();

            return new Result(generated, genDiagnostics, compileErrors);
        }

        /// <summary>Emits a fixed source via <c>RegisterSourceOutput</c> so it's hidden from other generators.</summary>
        private sealed class SiblingGenerator : IIncrementalGenerator
        {
            private readonly string _source;

            public SiblingGenerator(string source) => _source = source;

            public void Initialize(IncrementalGeneratorInitializationContext context) =>
                context.RegisterSourceOutput(context.CompilationProvider,
                    (spc, _) => spc.AddSource("Sibling.g.cs", _source));
        }

        private static ImmutableArray<MetadataReference> LoadReferences()
        {
            var tpa = (string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") ?? string.Empty;
            IEnumerable<MetadataReference> platform = tpa.Split(Path.PathSeparator)
                .Where(p => p.Length > 0 && File.Exists(p))
                .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p));

            // The GenFactory attributes assembly isn't part of the trusted platform assembly set (it's a
            // project reference, not a runtime assembly), so it's added explicitly.
            var attributes = MetadataReference.CreateFromFile(typeof(GenerateFactoryAttribute).Assembly.Location);

            return platform.Append(attributes).ToImmutableArray();
        }

        internal sealed class Result
        {
            public Result(string generated, ImmutableArray<Diagnostic> genDiagnostics, string[] compileErrors)
            {
                Generated = generated;
                GeneratorDiagnostics = genDiagnostics;
                CompileErrors = compileErrors;
            }

            public string Generated { get; }
            public ImmutableArray<Diagnostic> GeneratorDiagnostics { get; }
            public string[] CompileErrors { get; }

            public IEnumerable<Diagnostic> Errors =>
                GeneratorDiagnostics.Where(d => d.Severity == DiagnosticSeverity.Error);
        }
    }
}
