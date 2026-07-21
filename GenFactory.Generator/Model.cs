using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;

namespace GenFactory.Generator
{
    /// <summary>Role of a constructor parameter in the generated factory.</summary>
    internal enum ParamRole
    {
        /// <summary>Resolved from the DI container (becomes a factory field).</summary>
        Inject,

        /// <summary>Caller-supplied (becomes a Create parameter).</summary>
        Arg
    }

    internal sealed record CtorParam(
        string TypeFqn,
        string Name,
        ParamRole Role,
        bool IsOptional,
        string? DefaultLiteral,
        // "in" is always safe to preserve (no ordering constraint) on both the Create parameter and
        // the factory's own constructor parameter (for an Inject-role dependency).
        bool IsIn = false,
        // "params" can only be kept on the rendered Create signature when the param ends up last
        // after required ctor args, then property args, then optional args are concatenated — see
        // FactoryGenerator.BuildCreateParams. Meaningless for Inject role.
        bool IsParams = false);

    internal sealed record PropArg(string TypeFqn, string PropName, string ParamName);

    internal sealed record FactoryModel(
        string Namespace,
        string ClassFqn,
        string FactoryName,
        string InterfaceName,
        string ReturnTypeFqn,
        EquatableArray<CtorParam> CtorParams,
        EquatableArray<PropArg> PropArgs,
        string? Error,
        // Only carries a real location for error models; valid models use Location.None so the
        // happy-path cache key stays stable across unrelated edits.
        Location Location,
        // "public" or "internal": matches the target class's accessibility so the generated
        // interface/class never end up less accessible than a public Create() would require.
        string Accessibility = "public",
        // "" for a non-generic class, otherwise "<T1, T2, ...>" appended after the interface/class name.
        string TypeParams = "",
        // "" when there are no constraints, otherwise " where T1 : ... where T2 : ..." appended after
        // the type parameter list.
        string Constraints = "",
        // Number of type parameters; used to render the unbound-generic typeof(Foo<,>) syntax the
        // registry needs for open-generic factories.
        int Arity = 0,
        // Non-global using directives copied verbatim from the target class's source file(s). Needed
        // because a parameter/property type produced by another source generator resolves to an
        // IErrorTypeSymbol during this generator's pass (generators can't see each other's output),
        // so it renders as a bare, unqualifiable name (e.g. "DbContext"). Emitting the source's usings
        // lets that name bind in the final merged compilation; global::-qualified types are unaffected.
        EquatableArray<string> Usings = default);

    /// <summary>
    /// Value-equal wrapper around an immutable array so records containing it participate
    /// correctly in the incremental generator cache.
    /// </summary>
    internal readonly struct EquatableArray<T> : IEquatable<EquatableArray<T>>, IEnumerable<T>
        where T : IEquatable<T>
    {
        private readonly T[] _array;

        public EquatableArray(T[] array) => _array = array;

        public int Count => _array?.Length ?? 0;

        public T this[int index] => _array[index];

        public bool Equals(EquatableArray<T> other)
        {
            if (_array is null)
                return other._array is null;
            if (other._array is null)
                return false;
            if (_array.Length != other._array.Length)
                return false;
            for (int i = 0; i < _array.Length; i++)
            {
                if (!_array[i].Equals(other._array[i]))
                    return false;
            }

            return true;
        }

        public override bool Equals(object? obj) => obj is EquatableArray<T> other && Equals(other);

        public override int GetHashCode()
        {
            if (_array is null)
                return 0;
            unchecked
            {
                int hash = 17;
                foreach (T item in _array)
                    hash = hash * 31 + (item?.GetHashCode() ?? 0);
                return hash;
            }
        }

        public IEnumerator<T> GetEnumerator() => ((IEnumerable<T>)(_array ?? Array.Empty<T>())).GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        public static EquatableArray<T> From(IEnumerable<T> items) => new(items.ToArray());
    }
}
