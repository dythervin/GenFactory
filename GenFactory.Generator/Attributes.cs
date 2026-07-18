using System;

namespace GenFactory
{
    /// <summary>Generates an <c>I{Name}Factory</c>/<c>{Name}Factory</c> pair that constructs the decorated class.</summary>
    [AttributeUsage(AttributeTargets.Class)]
    public sealed class GenerateFactoryAttribute : Attribute
    {
        /// <summary>Overrides the generated factory's name (default: <c>{ClassName}Factory</c>).</summary>
        public string? FactoryName { get; set; }

        /// <summary>Overrides the type returned by the generated <c>Create</c> method.</summary>
        public Type? ReturnType { get; set; }
    }

    /// <summary>Marks a constructor parameter or settable property as a caller-supplied <c>Create</c> argument instead of a DI-resolved dependency.</summary>
    [AttributeUsage(AttributeTargets.Parameter | AttributeTargets.Property)]
    public sealed class FactoryArgAttribute : Attribute
    {
    }

    /// <summary>Selects which constructor the generated factory should use when a type has more than one public constructor.</summary>
    [AttributeUsage(AttributeTargets.Constructor)]
    public sealed class FactoryCtorAttribute : Attribute
    {
    }
}
