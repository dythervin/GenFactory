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

        public static Result Run(params string[] sources)
        {
            var trees = sources.Select(s => CSharpSyntaxTree.ParseText(s)).ToArray();
            var compilation = CSharpCompilation.Create("TestAsm", trees, References,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary,
                    nullableContextOptions: NullableContextOptions.Enable));

            var driver = (CSharpGeneratorDriver)CSharpGeneratorDriver
                .Create(new FactoryGenerator())
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

        private static ImmutableArray<MetadataReference> LoadReferences()
        {
            var tpa = (string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") ?? string.Empty;
            IEnumerable<MetadataReference> platform = tpa.Split(Path.PathSeparator)
                .Where(p => p.Length > 0 && File.Exists(p))
                .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p));

            // GenFactory.Attributes isn't part of the trusted platform assembly set (it's a
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
