using System.Linq;
using Microsoft.CodeAnalysis;
using Xunit;

namespace GenFactory.Tests
{
    public sealed class FactoryGeneratorTests
    {
        private static GeneratorHarness.Result Run(string source) => GeneratorHarness.Run(source);

        [Fact]
        public void GeneratesFactory_ForInjectOnlyCtor()
        {
            var result = Run(@"
namespace App
{
    public interface ILogger {}
    public class Logger : ILogger {}

    [GenFactory.GenerateFactory]
    public class Service
    {
        public Service(ILogger logger) {}
    }
}
");
            Assert.Empty(result.Errors);
            Assert.Empty(result.CompileErrors);
            Assert.Contains("public interface IServiceFactory", result.Generated);
            Assert.Contains("Create()", result.Generated);
            Assert.Contains("public sealed class ServiceFactory : IServiceFactory", result.Generated);
            Assert.Contains("private readonly global::App.ILogger _logger;", result.Generated);
            Assert.Contains("return new global::App.Service(_logger);", result.Generated);
        }

        [Fact]
        public void InjectedField_OfBuiltInType_IsGloballyQualified()
        {
            var result = Run(@"
namespace App
{
    [GenFactory.GenerateFactory]
    public class Service
    {
        public Service(int retries, string name) {}
    }
}
");
            Assert.Empty(result.Errors);
            Assert.Empty(result.CompileErrors);
            // Field declarations are always fully global::-qualified, even for built-in types.
            Assert.Contains("private readonly global::System.Int32 _retries;", result.Generated);
            Assert.Contains("private readonly global::System.String _name;", result.Generated);
        }

        [Fact]
        public void InjectedField_OfTypeFromAnotherGenerator_ResolvesViaCopiedUsing()
        {
            // DbContext exists only in a sibling generator's output, so FactoryGenerator sees it as an
            // unqualifiable error symbol. The target's using must be copied into the factory for the
            // bare name to bind in the final compilation.
            var target = @"
using Generated;
namespace App
{
    [GenFactory.GenerateFactory]
    public class Service
    {
        public Service(DbContext db) {}
    }
}";
            var sibling = @"
namespace Generated
{
    public class DbContext {}
}";
            var result = GeneratorHarness.RunWithSibling(sibling, target);

            Assert.Empty(result.Errors);
            Assert.Empty(result.CompileErrors);
            Assert.Contains("using Generated;", result.Generated);
            Assert.Contains("private readonly DbContext _db;", result.Generated);
        }

        [Fact]
        public void CopiedUsings_ExcludeGlobalUsings()
        {
            var result = Run(@"
global using System.Text;
using System.Collections.Generic;
namespace App
{
    public interface IThing {}
    public class Thing : IThing {}

    [GenFactory.GenerateFactory]
    public class Service
    {
        public Service(IThing thing) {}
    }
}
");
            Assert.Empty(result.Errors);
            Assert.Empty(result.CompileErrors);
            // Non-global usings are copied; global usings already apply assembly-wide, so they're skipped.
            Assert.Contains("using System.Collections.Generic;", result.Generated);
            Assert.DoesNotContain("global using", result.Generated);
        }

        [Fact]
        public void GeneratesFactory_WithRequiredFactoryArgCtorParam()
        {
            var result = Run(@"
namespace App
{
    [GenFactory.GenerateFactory]
    public class Service
    {
        public Service([GenFactory.FactoryArg] int count) {}
    }
}
");
            Assert.Empty(result.Errors);
            Assert.Empty(result.CompileErrors);
            Assert.Contains("Create(global::System.Int32 count)", result.Generated);
            Assert.Contains("return new global::App.Service(count);", result.Generated);
        }

        [Fact]
        public void GeneratesFactory_WithOptionalFactoryArg_UsesDefaultLiteral()
        {
            var result = Run(@"
namespace App
{
    [GenFactory.GenerateFactory]
    public class Service
    {
        public Service([GenFactory.FactoryArg] int count = 42) {}
    }
}
");
            Assert.Empty(result.Errors);
            Assert.Empty(result.CompileErrors);
            Assert.Contains("Create(global::System.Int32 count = 42)", result.Generated);
        }

        [Fact]
        public void RequiredArgsOrderedBeforeOptionalArgs()
        {
            var result = Run(@"
namespace App
{
    [GenFactory.GenerateFactory]
    public class Service
    {
        public Service([GenFactory.FactoryArg] int optional = 1, [GenFactory.FactoryArg] string required = null!) {}
    }
}
");
            // 'required' has a default too, so both are optional; still check they render together in order.
            Assert.Empty(result.Errors);
            Assert.Contains("Create(global::System.Int32 optional = 1, global::System.String required = null)", result.Generated);
        }

        [Fact]
        public void GeneratesFactory_WithFactoryArgProperty_UsesObjectInitializer()
        {
            var result = Run(@"
namespace App
{
    [GenFactory.GenerateFactory]
    public class Service
    {
        public Service() {}

        [GenFactory.FactoryArg]
        public int Count { get; set; }
    }
}
");
            Assert.Empty(result.Errors);
            Assert.Empty(result.CompileErrors);
            Assert.Contains("Create(global::System.Int32 count)", result.Generated);
            Assert.Contains("return new global::App.Service() { Count = count };", result.Generated);
        }

        [Fact]
        public void PropArgsRenderedAfterCtorRequiredArgs_AndBeforeOptionalArgs()
        {
            var result = Run(@"
namespace App
{
    [GenFactory.GenerateFactory]
    public class Service
    {
        public Service([GenFactory.FactoryArg] int required, [GenFactory.FactoryArg] int optional = 5) {}

        [GenFactory.FactoryArg]
        public int Extra { get; set; }
    }
}
");
            Assert.Empty(result.Errors);
            Assert.Contains("Create(global::System.Int32 required, global::System.Int32 extra, global::System.Int32 optional = 5)", result.Generated);
        }

        [Fact]
        public void Error_WhenFactoryArgPropertyHasNoSetter()
        {
            var result = Run(@"
namespace App
{
    [GenFactory.GenerateFactory]
    public class Service
    {
        public Service() {}

        [GenFactory.FactoryArg]
        public int Count { get; }
    }
}
");
            Assert.Contains(result.Errors, d => d.Id == "NFG001" && d.GetMessage().Contains("must be settable or init-only"));
        }

        [Fact]
        public void Error_WhenNoPublicConstructor()
        {
            var result = Run(@"
namespace App
{
    [GenFactory.GenerateFactory]
    public class Service
    {
        private Service() {}
    }
}
");
            Assert.Contains(result.Errors, d => d.Id == "NFG001" && d.GetMessage().Contains("no public constructor found"));
        }

        [Fact]
        public void Error_WhenMultipleConstructorsNoneMarked()
        {
            var result = Run(@"
namespace App
{
    [GenFactory.GenerateFactory]
    public class Service
    {
        public Service() {}
        public Service(int x) {}
    }
}
");
            Assert.Contains(result.Errors, d => d.Id == "NFG001" && d.GetMessage().Contains("mark one with [FactoryCtor]"));
        }

        [Fact]
        public void Error_WhenMultipleConstructorsMultipleMarked()
        {
            var result = Run(@"
namespace App
{
    [GenFactory.GenerateFactory]
    public class Service
    {
        [GenFactory.FactoryCtor]
        public Service() {}

        [GenFactory.FactoryCtor]
        public Service(int x) {}
    }
}
");
            Assert.Contains(result.Errors, d => d.Id == "NFG001" && d.GetMessage().Contains("multiple constructors marked"));
        }

        [Fact]
        public void SelectsMarkedConstructor_WhenMultipleConstructorsExist()
        {
            var result = Run(@"
namespace App
{
    [GenFactory.GenerateFactory]
    public class Service
    {
        public Service() {}

        [GenFactory.FactoryCtor]
        public Service([GenFactory.FactoryArg] int x) {}
    }
}
");
            Assert.Empty(result.Errors);
            Assert.Empty(result.CompileErrors);
            Assert.Contains("Create(global::System.Int32 x)", result.Generated);
            Assert.Contains("return new global::App.Service(x);", result.Generated);
        }

        [Fact]
        public void FactoryNameOverride_ChangesGeneratedNames()
        {
            var result = Run(@"
namespace App
{
    [GenFactory.GenerateFactory(FactoryName = ""CustomFactory"")]
    public class Service
    {
        public Service() {}
    }
}
");
            Assert.Empty(result.Errors);
            Assert.Empty(result.CompileErrors);
            Assert.Contains("public interface ICustomFactory", result.Generated);
            Assert.Contains("public sealed class CustomFactory : ICustomFactory", result.Generated);
        }

        [Fact]
        public void ReturnTypeOverride_UsesSpecifiedType()
        {
            var result = Run(@"
namespace App
{
    public interface IOther {}

    [GenFactory.GenerateFactory(ReturnType = typeof(IOther))]
    public class Service : IOther
    {
        public Service() {}
    }
}
");
            Assert.Empty(result.Errors);
            Assert.Empty(result.CompileErrors);
            Assert.Contains("global::App.IOther Create()", result.Generated);
        }

        [Fact]
        public void ResolveReturnType_PrefersSameNamespaceInterface()
        {
            var result = Run(@"
namespace App
{
    public interface IService {}

    [GenFactory.GenerateFactory]
    public class Service : IService
    {
        public Service() {}
    }
}

namespace Other
{
    public interface IService {}
}
");
            Assert.Empty(result.Errors);
            Assert.Empty(result.CompileErrors);
            Assert.Contains("global::App.IService Create()", result.Generated);
        }

        [Fact]
        public void ResolveReturnType_FallsBackToOtherNamespaceInterface()
        {
            var result = Run(@"
namespace App
{
    [GenFactory.GenerateFactory]
    public class Service : Other.IService
    {
        public Service() {}
    }
}

namespace Other
{
    public interface IService {}
}
");
            Assert.Empty(result.Errors);
            Assert.Empty(result.CompileErrors);
            Assert.Contains("global::Other.IService Create()", result.Generated);
        }

        [Fact]
        public void ResolveReturnType_FallsBackToClassItself_WhenNoMatchingInterface()
        {
            var result = Run(@"
namespace App
{
    [GenFactory.GenerateFactory]
    public class Service
    {
        public Service() {}
    }
}
");
            Assert.Empty(result.Errors);
            Assert.Empty(result.CompileErrors);
            Assert.Contains("global::App.Service Create()", result.Generated);
        }

        [Fact]
        public void DefaultLiteral_RendersVariousTypes()
        {
            var result = Run(@"
namespace App
{
    public enum Color { Red, Green, Blue }

    [GenFactory.GenerateFactory]
    public class Service
    {
        public Service(
            [GenFactory.FactoryArg] bool flag = true,
            [GenFactory.FactoryArg] string text = ""hi"",
            [GenFactory.FactoryArg] char letter = 'x',
            [GenFactory.FactoryArg] decimal amount = 3.5m,
            [GenFactory.FactoryArg] Color color = Color.Green,
            [GenFactory.FactoryArg] string? none = null) {}
    }
}
");
            Assert.Empty(result.Errors);
            Assert.Empty(result.CompileErrors);
            Assert.Contains("global::System.Boolean flag = true", result.Generated);
            Assert.Contains("global::System.String text = \"hi\"", result.Generated);
            Assert.Contains("global::System.Char letter = 'x'", result.Generated);
            Assert.Contains("global::System.Decimal amount = 3.5m", result.Generated);
            Assert.Contains("(global::App.Color)(1)", result.Generated);
            Assert.Contains("global::System.String? none = null", result.Generated);
        }

        [Fact]
        public void GlobalNamespace_ClassWithoutNamespace_GeneratesCorrectly()
        {
            var result = Run(@"
[GenFactory.GenerateFactory]
public class Service
{
    public Service() {}
}
");
            Assert.Empty(result.Errors);
            Assert.Empty(result.CompileErrors);
            Assert.Contains("public interface IServiceFactory", result.Generated);
            Assert.DoesNotContain("namespace ", result.Generated.Substring(0, result.Generated.IndexOf("IServiceFactory")));
        }

        [Fact]
        public void Registry_CollectsMultipleFactories_OrderedByNamespaceThenName()
        {
            var result = Run(@"
namespace Zeta
{
    [GenFactory.GenerateFactory]
    public class Bravo { public Bravo() {} }

    [GenFactory.GenerateFactory]
    public class Alpha { public Alpha() {} }
}

namespace Alpha
{
    [GenFactory.GenerateFactory]
    public class Solo { public Solo() {} }
}
");
            Assert.Empty(result.Errors);
            Assert.Empty(result.CompileErrors);
            Assert.Contains("GeneratedFactoryRegistry", result.Generated);

            int alphaNsIdx = result.Generated.IndexOf("typeof(global::Alpha.ISoloFactory)");
            int zetaAlphaIdx = result.Generated.IndexOf("typeof(global::Zeta.IAlphaFactory)");
            int zetaBravoIdx = result.Generated.IndexOf("typeof(global::Zeta.IBravoFactory)");

            Assert.True(alphaNsIdx >= 0 && zetaAlphaIdx >= 0 && zetaBravoIdx >= 0);
            Assert.True(alphaNsIdx < zetaAlphaIdx);
            Assert.True(zetaAlphaIdx < zetaBravoIdx);
        }

        [Fact]
        public void Registry_NotEmitted_WhenOnlyErrorModelsExist()
        {
            var result = Run(@"
namespace App
{
    [GenFactory.GenerateFactory]
    public class Service
    {
        private Service() {}
    }
}
");
            Assert.DoesNotContain("GeneratedFactoryRegistry", result.Generated);
        }

        [Fact]
        public void Registry_EmitsProviderBridge_WhenMarkerTypePresent()
        {
            var result = Run(@"
namespace Zenject
{
    public sealed class BindStmt
    {
        public ToStmt To(System.Type t) => new ToStmt();
    }

    public sealed class ToStmt
    {
        public void AsSingle() {}
    }

    public sealed class DiContainer
    {
        public BindStmt Bind(System.Type t) => new BindStmt();
    }
}

namespace App
{
    [GenFactory.GenerateFactory]
    public class Service
    {
        public Service() {}
    }
}
");
            Assert.Empty(result.Errors);
            Assert.Empty(result.CompileErrors);
            Assert.Contains("GeneratedFactoryRegistryZenjectExtensions", result.Generated);
            Assert.Contains("RegisterGeneratedFactories(this global::Zenject.DiContainer builder", result.Generated);
            Assert.Contains("builder.Bind(r.InterfaceType).To(r.ImplementationType).AsSingle();", result.Generated);
        }

        [Fact]
        public void Registry_VContainerBridge_RegistersOpenGenericFactory()
        {
            var result = Run(@"
namespace VContainer
{
    public enum Lifetime { Singleton, Scoped, Transient }

    public sealed class RegistrationBuilder
    {
        public RegistrationBuilder As(System.Type t) => this;
    }

    public sealed class IContainerBuilder
    {
        public RegistrationBuilder Register(System.Type t, Lifetime lifetime) => new RegistrationBuilder();
    }
}

namespace App
{
    [GenFactory.GenerateFactory]
    public class Repo<T>
    {
        public Repo() {}
    }
}
");
            Assert.Empty(result.Errors);
            Assert.Empty(result.CompileErrors);
            Assert.Contains("typeof(global::App.IRepoFactory<>)", result.Generated);
            Assert.Contains("builder.Register(r.ImplementationType, lifetime).As(r.InterfaceType);", result.Generated);
        }

        [Fact]
        public void Registry_MicrosoftDiBridge_RegistersOpenGenericFactory()
        {
            var result = Run(@"
namespace Microsoft.Extensions.DependencyInjection
{
    public enum ServiceLifetime { Singleton, Scoped, Transient }

    public sealed class ServiceDescriptor
    {
        public ServiceDescriptor(System.Type serviceType, System.Type implementationType, ServiceLifetime lifetime) {}
    }

    public sealed class IServiceCollection
    {
        public void Add(ServiceDescriptor descriptor) {}
    }
}

namespace App
{
    [GenFactory.GenerateFactory]
    public class Repo<T>
    {
        public Repo() {}
    }
}
");
            Assert.Empty(result.Errors);
            Assert.Empty(result.CompileErrors);
            Assert.Contains("typeof(global::App.IRepoFactory<>)", result.Generated);
            Assert.Contains(
                "builder.Add(new global::Microsoft.Extensions.DependencyInjection.ServiceDescriptor(r.InterfaceType, r.ImplementationType, lifetime));",
                result.Generated);
        }

        [Fact]
        public void Registry_ZenjectBridge_RegistersOpenGenericFactory()
        {
            var result = Run(@"
namespace Zenject
{
    public sealed class BindStmt
    {
        public ToStmt To(System.Type t) => new ToStmt();
    }

    public sealed class ToStmt
    {
        public void AsSingle() {}
    }

    public sealed class DiContainer
    {
        public BindStmt Bind(System.Type t) => new BindStmt();
    }
}

namespace App
{
    [GenFactory.GenerateFactory]
    public class Repo<T>
    {
        public Repo() {}
    }
}
");
            Assert.Empty(result.Errors);
            Assert.Empty(result.CompileErrors);
            Assert.Contains("typeof(global::App.IRepoFactory<>)", result.Generated);
            Assert.Contains("builder.Bind(r.InterfaceType).To(r.ImplementationType).AsSingle();", result.Generated);
        }

        [Fact]
        public void Registry_NoProviderBridge_WhenMarkerTypeAbsent()
        {
            var result = Run(@"
namespace App
{
    [GenFactory.GenerateFactory]
    public class Service
    {
        public Service() {}
    }
}
");
            Assert.Empty(result.Errors);
            Assert.Empty(result.CompileErrors);
            Assert.DoesNotContain("Extensions", result.Generated);
        }

        [Fact]
        public void InternalClass_EmitsInternalFactory()
        {
            var result = Run(@"
namespace App
{
    [GenFactory.GenerateFactory]
    internal class Service
    {
        public Service() {}
    }
}
");
            Assert.Empty(result.Errors);
            Assert.Empty(result.CompileErrors);
            Assert.Contains("internal interface IServiceFactory", result.Generated);
            Assert.Contains("internal sealed class ServiceFactory : IServiceFactory", result.Generated);
        }

        [Fact]
        public void Error_WhenNestedInsideGenericType()
        {
            var result = Run(@"
namespace App
{
    public class Outer<T>
    {
        [GenFactory.GenerateFactory]
        public class Service
        {
            public Service([GenFactory.FactoryArg] T value) {}
        }
    }
}
");
            Assert.Contains(result.Errors, d => d.Id == "NFG001" && d.GetMessage().Contains("nested in a generic type"));
        }

        [Fact]
        public void Error_WhenNestedTypeIsPrivate()
        {
            var result = Run(@"
namespace App
{
    public class Outer
    {
        [GenFactory.GenerateFactory]
        private class Service
        {
            public Service() {}
        }
    }
}
");
            Assert.Contains(result.Errors, d => d.Id == "NFG001" && d.GetMessage().Contains("can't reach it"));
        }

        [Fact]
        public void Error_WhenNestedTypeIsProtected()
        {
            var result = Run(@"
namespace App
{
    public class Outer
    {
        [GenFactory.GenerateFactory]
        protected class Service
        {
            public Service() {}
        }
    }
}
");
            Assert.Contains(result.Errors, d => d.Id == "NFG001" && d.GetMessage().Contains("can't reach it"));
        }

        [Fact]
        public void NestedInternalType_UnderPublicOuter_EmitsInternalFactory()
        {
            var result = Run(@"
namespace App
{
    public class Outer
    {
        [GenFactory.GenerateFactory]
        internal class Service
        {
            public Service() {}
        }
    }
}
");
            Assert.Empty(result.Errors);
            Assert.Empty(result.CompileErrors);
            Assert.Contains("internal interface IServiceFactory", result.Generated);
        }

        [Fact]
        public void GenericClass_SingleTypeParam_GeneratesGenericFactory()
        {
            var result = Run(@"
namespace App
{
    public interface IRepo<T> {}

    [GenFactory.GenerateFactory]
    public class Service<T>
    {
        public Service(IRepo<T> repo, [GenFactory.FactoryArg] T seed) {}
    }
}
");
            Assert.Empty(result.Errors);
            Assert.Empty(result.CompileErrors);
            Assert.Contains("public interface IServiceFactory<T>", result.Generated);
            Assert.Contains("public sealed class ServiceFactory<T> : IServiceFactory<T>", result.Generated);
            Assert.Contains("Create(T seed)", result.Generated);
            Assert.Contains("return new global::App.Service<T>(_repo, seed);", result.Generated);
        }

        [Fact]
        public void GenericClass_WithConstraints_PropagatesConstraints()
        {
            var result = Run(@"
namespace App
{
    [GenFactory.GenerateFactory]
    public class Service<T> where T : class, new()
    {
        public Service() {}
    }
}
");
            Assert.Empty(result.Errors);
            Assert.Empty(result.CompileErrors);
            Assert.Contains("interface IServiceFactory<T> where T : class, new()", result.Generated);
            Assert.Contains("class ServiceFactory<T> : IServiceFactory<T> where T : class, new()", result.Generated);
        }

        [Fact]
        public void GenericClass_MultipleTypeParams_GeneratesFactoryAndOpenGenericRegistryEntry()
        {
            var result = Run(@"
namespace App
{
    [GenFactory.GenerateFactory]
    public class Pair<TKey, TValue>
    {
        public Pair([GenFactory.FactoryArg] TKey key, [GenFactory.FactoryArg] TValue value) {}
    }
}
");
            Assert.Empty(result.Errors);
            Assert.Empty(result.CompileErrors);
            Assert.Contains("public interface IPairFactory<TKey, TValue>", result.Generated);
            Assert.Contains("public sealed class PairFactory<TKey, TValue> : IPairFactory<TKey, TValue>", result.Generated);
            Assert.Contains("Create(TKey key, TValue value)", result.Generated);
            Assert.Contains("typeof(global::App.IPairFactory<,>)", result.Generated);
            Assert.Contains("typeof(global::App.PairFactory<,>)", result.Generated);
        }

        [Fact]
        public void NestedDependencyType_IsFullyQualified()
        {
            var result = Run(@"
namespace App
{
    public class Outer
    {
        public class Dep {}

        [GenFactory.GenerateFactory]
        public class Service
        {
            public Service([GenFactory.FactoryArg] Dep dep) {}
        }
    }
}
");
            Assert.Empty(result.Errors);
            Assert.Empty(result.CompileErrors);
            Assert.Contains("Create(global::App.Outer.Dep dep)", result.Generated);
        }

        [Fact]
        public void Error_WhenInjectedParamsCollideOnFieldName()
        {
            var result = Run(@"
namespace App
{
    public interface ILogger {}

    [GenFactory.GenerateFactory]
    public class Service
    {
        public Service(ILogger logger, ILogger Logger) {}
    }
}
");
            Assert.Contains(result.Errors, d => d.Id == "NFG001" && d.GetMessage().Contains("map to the same field"));
        }

        [Fact]
        public void Error_WhenCreateParamsCollide_CtorArgAndProperty()
        {
            var result = Run(@"
namespace App
{
    [GenFactory.GenerateFactory]
    public class Service
    {
        public Service([GenFactory.FactoryArg] int id) {}

        [GenFactory.FactoryArg]
        public int Id { get; set; }
    }
}
");
            Assert.Contains(result.Errors, d => d.Id == "NFG001" && d.GetMessage().Contains("multiple Create parameters are named 'id'"));
        }

        [Fact]
        public void Error_WhenRefCtorParam()
        {
            var result = Run(@"
namespace App
{
    [GenFactory.GenerateFactory]
    public class Service
    {
        public Service(ref int count) {}
    }
}
");
            Assert.Contains(result.Errors, d => d.Id == "NFG001" && d.GetMessage().Contains("uses 'ref'"));
        }

        [Fact]
        public void Error_WhenOutCtorParam()
        {
            var result = Run(@"
namespace App
{
    [GenFactory.GenerateFactory]
    public class Service
    {
        public Service(out int count) { count = 0; }
    }
}
");
            Assert.Contains(result.Errors, d => d.Id == "NFG001" && d.GetMessage().Contains("uses 'out'"));
        }

        [Fact]
        public void InCtorParam_PreservesInOnCreateSignature()
        {
            var result = Run(@"
namespace App
{
    public struct Point { public int X; public int Y; }

    [GenFactory.GenerateFactory]
    public class Service
    {
        public Service([GenFactory.FactoryArg] in Point origin) {}
    }
}
");
            Assert.Empty(result.Errors);
            Assert.Empty(result.CompileErrors);
            Assert.Contains("Create(in global::App.Point origin)", result.Generated);
            Assert.Contains("return new global::App.Service(origin);", result.Generated);
        }

        [Fact]
        public void InCtorParam_OnInjectedDependency_PreservesInOnFactoryCtor()
        {
            var result = Run(@"
namespace App
{
    public struct Point { public int X; public int Y; }

    [GenFactory.GenerateFactory]
    public class Service
    {
        public Service(in Point origin) {}
    }
}
");
            Assert.Empty(result.Errors);
            Assert.Empty(result.CompileErrors);
            Assert.Contains("public ServiceFactory(in global::App.Point origin)", result.Generated);
            Assert.Contains("private readonly global::App.Point _origin;", result.Generated);
        }

        [Fact]
        public void ParamsCtorArg_LastInSignature_PreservesParamsKeyword()
        {
            var result = Run(@"
namespace App
{
    [GenFactory.GenerateFactory]
    public class Service
    {
        public Service([GenFactory.FactoryArg] params int[] values) {}
    }
}
");
            Assert.Empty(result.Errors);
            Assert.Empty(result.CompileErrors);
            Assert.Contains("Create(params global::System.Int32[] values)", result.Generated);
        }

        [Fact]
        public void ParamsCtorArg_BumpedByPropArg_DegradesToPlainArray()
        {
            var result = Run(@"
namespace App
{
    [GenFactory.GenerateFactory]
    public class Service
    {
        public Service([GenFactory.FactoryArg] params int[] values) {}

        [GenFactory.FactoryArg]
        public int Extra { get; set; }
    }
}
");
            Assert.Empty(result.Errors);
            Assert.Empty(result.CompileErrors);
            Assert.Contains("Create(global::System.Int32[] values, global::System.Int32 extra)", result.Generated);
            Assert.DoesNotContain("params global::System.Int32[] values", result.Generated);
        }
    }
}
