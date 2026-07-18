using System;

namespace GenFactory.Generator
{
    /// <summary>
    /// Describes one DI container the generator can emit a <c>RegisterGeneratedFactories</c>
    /// bridge for. A bridge is emitted only when <see cref="MarkerType"/> is resolvable in the
    /// compilation (i.e. the assembly references that container).
    /// </summary>
    internal sealed class DiProvider
    {
        /// <summary>Display name (diagnostics/comments only).</summary>
        public string Name { get; }

        /// <summary>Fully-qualified metadata name used to detect the container's presence.</summary>
        public string MarkerType { get; }

        /// <summary>The builder/container type the extension method extends (global::-qualified).</summary>
        public string BuilderType { get; }

        /// <summary>Namespace to import for the registration call (e.g. extension methods), or null.</summary>
        public string? Using { get; }

        /// <summary>The container's lifetime enum type (global::-qualified), or null if it has none.</summary>
        public string? LifetimeType { get; }

        /// <summary>Default lifetime member (global::-qualified), or null when <see cref="LifetimeType"/> is null.</summary>
        public string? LifetimeDefault { get; }

        /// <summary>Whether the container's registration API is verified against a real package in this repo.</summary>
        public bool Verified { get; }

        /// <summary>
        /// Emits the single registration statement for one factory.
        /// Args: (implExpr, interfaceExpr, lifetimeExpr). lifetimeExpr is empty when the container has no lifetime enum.
        /// </summary>
        public Func<string, string, string, string> RegisterLine { get; }

        public DiProvider(string name, string markerType, string builderType, string? @using,
            string? lifetimeType, string? lifetimeDefault, bool verified,
            Func<string, string, string, string> registerLine)
        {
            Name = name;
            MarkerType = markerType;
            BuilderType = builderType;
            Using = @using;
            LifetimeType = lifetimeType;
            LifetimeDefault = lifetimeDefault;
            Verified = verified;
            RegisterLine = registerLine;
        }

        /// <summary>
        /// All supported containers. Add a new one here — no other change is required, as long as the
        /// container's registration API fits the shared (implementation, interface, lifetime) triple
        /// RegisterLine expresses.
        /// Each RegisterLine also correctly registers open-generic factories (a [GenerateFactory]
        /// class with type parameters): VContainer's Register(Type, Lifetime).As(Type), MEDI's
        /// ServiceDescriptor(Type, Type, Lifetime), and Zenject's Bind(Type).To(Type) all accept the
        /// unbound generic Type objects the registry emits.
        /// </summary>
        public static readonly DiProvider[] All =
        {
            // VContainer — verified against jp.hadashikick.vcontainer in this repo.
            new("VContainer",
                markerType: "VContainer.IContainerBuilder",
                builderType: "global::VContainer.IContainerBuilder",
                @using: "VContainer",
                lifetimeType: "global::VContainer.Lifetime",
                lifetimeDefault: "global::VContainer.Lifetime.Singleton",
                verified: true,
                registerLine: (impl, iface, lt) => $"builder.Register({impl}, {lt}).As({iface});"),

            // Microsoft.Extensions.DependencyInjection — verified against 8.0.0 in this repo.
            new("Microsoft.Extensions.DependencyInjection",
                markerType: "Microsoft.Extensions.DependencyInjection.IServiceCollection",
                builderType: "global::Microsoft.Extensions.DependencyInjection.IServiceCollection",
                @using: null,
                lifetimeType: "global::Microsoft.Extensions.DependencyInjection.ServiceLifetime",
                lifetimeDefault: "global::Microsoft.Extensions.DependencyInjection.ServiceLifetime.Singleton",
                verified: true,
                registerLine: (impl, iface, lt) =>
                    $"builder.Add(new global::Microsoft.Extensions.DependencyInjection.ServiceDescriptor({iface}, {impl}, {lt}));"),

            // Zenject / Extenject — best-effort. Zenject has no lifetime enum; factories bind AsSingle.
            new("Zenject",
                markerType: "Zenject.DiContainer",
                builderType: "global::Zenject.DiContainer",
                @using: null,
                lifetimeType: null,
                lifetimeDefault: null,
                verified: false,
                registerLine: (impl, iface, lt) => $"builder.Bind({iface}).To({impl}).AsSingle();"),
        };
    }
}
