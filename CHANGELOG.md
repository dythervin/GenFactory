# Changelog

All notable changes to this project are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [0.2.1] - 2026-07-21

### Fixed

- A constructor/property type authored in another source generator's output no longer breaks the
  generated factory. Such a type is an `IErrorTypeSymbol` during this generator's pass (generators
  can't see each other's output), so it renders as a bare, unqualifiable name (e.g. `DbContext`) and
  previously failed with CS0246. The target class's non-global `using` directives are now copied into
  the generated factory so the name binds in the final merged compilation; `global::`-qualified types
  are unaffected.

## [0.2.0] - 2026-07-21

### Changed

- Every type reference in generated code — fields, constructor parameters, `Create` parameters,
  and default-value literals — is now fully `global::`-qualified, including built-in types
  (`global::System.Int32` rather than the `int` keyword), so a generated type reference can never be
  shadowed by a type of the same name in the consumer's scope.
- Renamed the attributes project and assembly from `GenFactory.Attributes` to `GenFactory`; the
  runtime assembly consumers compile against is now `GenFactory.dll`. The attribute namespace
  (`GenFactory`) and the NuGet package id (`Dythervin.GenFactory`) are unchanged.

## [0.1.0] - 2026-07-18

### Added

- `[GenerateFactory]` source generator emitting an `I{Name}Factory` / `{Name}Factory` pair per
  decorated class: the constructor takes DI-resolved dependencies, `Create(...)` takes the
  caller-supplied `[FactoryArg]` values.
- `[FactoryArg]` on constructor parameters and settable/init-only properties; `[FactoryCtor]` to
  select a constructor when a class has more than one.
- Per-assembly `GeneratedFactoryRegistry` plus `RegisterGeneratedFactories` bridge extensions for
  VContainer, `Microsoft.Extensions.DependencyInjection`, and Zenject.
- Open-generic factory support end to end.
- NuGet package (`Dythervin.GenFactory`) bundling the generator as an analyzer with the attributes
  assembly.
- Unity UPM package (`com.dythervin.genfactory`), installable via git URL or OpenUPM.

[Unreleased]: https://github.com/dythervin/GenFactory/compare/v0.2.1...HEAD
[0.2.1]: https://github.com/dythervin/GenFactory/compare/v0.2.0...v0.2.1
[0.2.0]: https://github.com/dythervin/GenFactory/compare/v0.1.0...v0.2.0
[0.1.0]: https://github.com/dythervin/GenFactory/releases/tag/v0.1.0
