# GenFactory

A C# incremental source generator that turns a decorated class into a factory: an
`I{Name}Factory` / `{Name}Factory` pair whose constructor takes only the class's DI-resolved
dependencies, and whose `Create(...)` method takes only the caller-supplied arguments. It also
emits a DI-agnostic registry of every generated factory, plus a `RegisterGeneratedFactories`
bridge extension for whichever DI containers your project actually references.

## Example

```csharp
using GenFactory;

namespace Game.Enemies
{
    public interface IEnemy
    {
        void Attack();
    }

    [GenerateFactory]
    public class Enemy : IEnemy
    {
        private readonly IWeaponService _weapons;
        private readonly int _health;

        public Enemy(IWeaponService weapons, [FactoryArg] int health)
        {
            _weapons = weapons;
            _health = health;
        }

        public void Attack() { }
    }
}
```

Generates:

```csharp
namespace Game.Enemies
{
    public interface IEnemyFactory
    {
        IEnemy Create(int health);
    }

    public sealed class EnemyFactory : IEnemyFactory
    {
        private readonly IWeaponService _weapons;

        public EnemyFactory(IWeaponService weapons) => _weapons = weapons;

        public IEnemy Create(int health) => new Enemy(_weapons, health);
    }
}
```

`Create`'s return type defaults to the `I{ClassName}` interface implemented by the class (falling
back to another namespace's same-named interface, then to the class itself). Every constructor
parameter is either DI-resolved (default) or a `Create` argument (marked `[FactoryArg]`);
`[FactoryArg]` also works on settable/init-only properties, which become extra `Create` parameters
assigned via an object initializer.

## Attributes

| Attribute | Target | Purpose |
|---|---|---|
| `[GenerateFactory]` | class | Opts the class into factory generation. `FactoryName` overrides the generated name; `ReturnType` overrides the inferred return type. |
| `[FactoryArg]` | ctor parameter or property | Caller-supplied via `Create(...)` instead of DI-resolved. |
| `[FactoryCtor]` | constructor | Selects which constructor to use when a class has more than one public constructor. |

## Registry and DI bridges

Every `[GenerateFactory]` class in an assembly is collected into a generated
`GeneratedFactoryRegistry` (namespace `{AssemblyName}.Generated`). If the compilation references
one of the supported DI containers, a matching `RegisterGeneratedFactories(namespacePrefix, ...)`
extension method is emitted alongside it:

| Container | Status |
|---|---|
| [VContainer](https://github.com/hadashiA/VContainer) | Verified |
| `Microsoft.Extensions.DependencyInjection` | Verified |
| [Zenject](https://github.com/modesttree/Zenject) / Extenject | Best-effort |

All three also register open-generic factories correctly: each container's registration API
(`Register(Type, Lifetime).As(Type)`, `new ServiceDescriptor(Type, Type, Lifetime)`, and
`Bind(Type).To(Type)` respectively) accepts the unbound generic `Type` objects the registry emits
for a `[GenerateFactory]` class with type parameters.

Adding a new container is a matter of appending one entry to `DiProvider.All` in
`GenFactory.Generator/DiProviders.cs`.

## Project layout

| Path | Purpose |
|---|---|
| `GenFactory.Generator/` | The incremental source generator (`netstandard2.0`, `IIncrementalGenerator`). |
| `GenFactory/` | The `[GenerateFactory]` / `[FactoryArg]` / `[FactoryCtor]` attributes, as a separate assembly so consumers don't pull in the analyzer as a normal reference. Its `Attributes.cs` is a linked copy of `GenFactory.Generator/Attributes.cs` — one source of truth, kept in sync at compile time. |
| `GenFactory.Tests/` | xUnit tests that run the generator in-memory via `GeneratorHarness` and assert on the generated source. |
| `Unity/` | A git-linkable UPM package: `Runtime/` compiles the attributes directly from source via an `asmdef`, `RoslynAnalyzers/` holds the built generator DLL (its `.meta` is checked in with the `RoslynAnalyzer` label already set). |
| `Unity.targets` | MSBuild logic that keeps `Unity/` in sync with build output (Release builds only). |

## Building and testing

```
dotnet build GenFactory.sln
dotnet test GenFactory.sln
```

A `Release` build of `GenFactory.Generator` also syncs `Unity/Runtime/Attributes.cs` and
`Unity/RoslynAnalyzers/GenFactory.Generator.dll` from the freshly built output. Run
`dotnet build GenFactory.Generator/GenFactory.Generator.csproj -c Release` and commit the result
before tagging a release, or the Unity package will ship a stale generator. CI (`.github/workflows/ci.yml`)
rebuilds Release and fails if `Unity/` doesn't match, so a stale sync won't merge unnoticed.

## Installation

### NuGet (.NET / SDK-style projects)

```
dotnet add package Dythervin.GenFactory
```

The package bundles the source generator as a Roslyn analyzer and the `[GenerateFactory]` /
`[FactoryArg]` / `[FactoryCtor]` attributes assembly, so a single reference is all you need — the
generator runs automatically.

### Unity — OpenUPM (recommended)

```
openupm add com.dythervin.genfactory
```

Or add the scoped registry manually in `Packages/manifest.json`:

```json
{
  "scopedRegistries": [
    {
      "name": "package.openupm.com",
      "url": "https://package.openupm.com",
      "scopes": ["com.dythervin"]
    }
  ],
  "dependencies": {
    "com.dythervin.genfactory": "0.1.0"
  }
}
```

### Unity — git URL

Package Manager → "Add package from git URL", pointing at this repo's `Unity` subfolder:

```
https://github.com/dythervin/GenFactory.git?path=/Unity
```

The generator DLL is already labeled as a `RoslynAnalyzer`, so it works as soon as it's imported —
no manual setup.
