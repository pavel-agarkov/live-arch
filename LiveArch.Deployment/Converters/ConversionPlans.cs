using System;
using System.Collections.Generic;

namespace LiveArch.Deployment.Converters
{
    /// <summary>
    /// Describes a resolved conversion plan that can be executed or inspected later.
    /// </summary>
    /// <param name="RootStep">Root conversion step to execute for the request.</param>
    public sealed record ConversionPlan(ConversionStep RootStep);

    /// <summary>
    /// Represents a data-only conversion step in a resolved plan.
    /// </summary>
    public abstract record ConversionStep;

    /// <summary>
    /// Represents a pass-through conversion when the source already satisfies the target type.
    /// </summary>
    public sealed record AssignableConversionStep : ConversionStep;

    /// <summary>
    /// Represents scalar string, boolean, or numeric conversion behavior.
    /// </summary>
    public sealed record PrimitiveConversionStep : ConversionStep;

    /// <summary>
    /// Represents conversion into a Pulumi enum value.
    /// </summary>
    public sealed record PulumiEnumConversionStep : ConversionStep;

    /// <summary>
    /// Represents conversion through an implicit CLR or Pulumi operator.
    /// </summary>
    /// <param name="TargetType">Target type wrapped by the implicit operator.</param>
    public sealed record ImplicitOperatorConversionStep(Type TargetType) : ConversionStep;

    /// <summary>
    /// Represents conversion into <c>Input&lt;T&gt;</c> by converting the inner value first.
    /// </summary>
    /// <param name="InnerType">Inner input type.</param>
    /// <param name="InnerStep">Plan used to produce the inner value.</param>
    public sealed record InputConversionStep(Type InnerType, ConversionStep InnerStep) : ConversionStep;

    /// <summary>
    /// Represents conversion into <c>InputList&lt;T&gt;</c> by converting each element.
    /// </summary>
    /// <param name="ElementType">Element type expected by the input list.</param>
    /// <param name="ElementStep">Plan used for each element conversion.</param>
    public sealed record InputListConversionStep(Type ElementType, ConversionStep ElementStep) : ConversionStep;

    /// <summary>
    /// Represents conversion into <c>ImmutableArray&lt;T&gt;</c> by converting each element.
    /// </summary>
    /// <param name="ElementType">Element type expected by the immutable array.</param>
    /// <param name="ElementStep">Plan used for each element conversion.</param>
    public sealed record ImmutableArrayConversionStep(Type ElementType, ConversionStep ElementStep) : ConversionStep;

    /// <summary>
    /// Represents conversion into <c>ImmutableDictionary&lt;string, TValue&gt;</c> by converting each value.
    /// </summary>
    /// <param name="ValueType">Dictionary value type.</param>
    /// <param name="ValueStep">Plan used for each dictionary value conversion.</param>
    public sealed record ImmutableDictionaryConversionStep(Type ValueType, ConversionStep ValueStep) : ConversionStep;

    /// <summary>
    /// Represents conversion into a Pulumi <c>Union&lt;string, TEnum&gt;</c> where TEnum is a Pulumi enum type.
    /// Tries string first, then enum conversion.
    /// </summary>
    /// <param name="EnumType">The Pulumi enum type (second union argument).</param>
    /// <param name="EnumStep">Plan used to convert source into the enum type.</param>
    public sealed record StringEnumUnionConversionStep(Type EnumType, ConversionStep EnumStep) : ConversionStep;

    /// <summary>
    /// Represents conversion into a Pulumi <c>InputUnion&lt;string, TEnum&gt;</c> where TEnum is a Pulumi enum type.
    /// </summary>
    /// <param name="EnumType">The Pulumi enum type (second union argument).</param>
    /// <param name="EnumStep">Plan used to convert source into the enum type.</param>
    public sealed record StringEnumInputUnionConversionStep(Type EnumType, ConversionStep EnumStep) : ConversionStep;

    /// <summary>
    /// Represents conversion of an <c>Output&lt;T&gt;</c> source by projecting its inner value through another step.
    /// </summary>
    /// <param name="ProjectedTargetType">Inner projected target type used during output projection.</param>
    /// <param name="InnerStep">Plan used to convert each resolved output value.</param>
    public sealed record ProjectedOutputConversionStep(Type ProjectedTargetType, ConversionStep InnerStep) : ConversionStep;

    /// <summary>
    /// Represents explicit execution of a named converter resolved from dependency injection.
    /// </summary>
    /// <param name="ConverterImplementationType">Resolved named converter implementation type.</param>
    /// <param name="TargetType">Requested target type for the named conversion.</param>
    public sealed record NamedConverterStep(Type ConverterImplementationType, Type TargetType) : ConversionStep;

    /// <summary>
    /// Represents keyed-list item fallback conversion by creating the target item and populating its <c>Value</c> property.
    /// </summary>
    /// <param name="ItemType">The target item type to instantiate.</param>
    /// <param name="ValuePropertyType">The type of the <c>Value</c> property.</param>
    /// <param name="ValueStep">Plan used to convert the source value for the <c>Value</c> property.</param>
    public sealed record KeyedListItemConversionStep(Type ItemType, Type ValuePropertyType, ConversionStep ValueStep) : ConversionStep;
}
