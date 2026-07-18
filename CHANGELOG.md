# Changelog

All notable changes to this project are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

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

[Unreleased]: https://github.com/dythervin/GenFactory/compare/v0.1.0...HEAD
[0.1.0]: https://github.com/dythervin/GenFactory/releases/tag/v0.1.0
