using LiveArch.Deployment.Converters;
using System;

namespace LiveArch.Deployment.Export.CSharp
{
    internal static class ConversionStepInspector
    {
        public static bool IsStructurallyInspectable(ConversionStep step)
        {
            ArgumentNullException.ThrowIfNull(step);

            return step switch
            {
                AssignableConversionStep => true,
                PrimitiveConversionStep => true,
                PulumiEnumConversionStep => true,
                ImplicitOperatorConversionStep => true,
                InputConversionStep input => IsStructurallyInspectable(input.InnerStep),
                InputListConversionStep list => IsStructurallyInspectable(list.ElementStep),
                ImmutableArrayConversionStep array => IsStructurallyInspectable(array.ElementStep),
                ImmutableDictionaryConversionStep dictionary => IsStructurallyInspectable(dictionary.ValueStep),
                StringEnumUnionConversionStep union => IsStructurallyInspectable(union.EnumStep),
                StringEnumInputUnionConversionStep inputUnion => IsStructurallyInspectable(inputUnion.EnumStep),
                ProjectedOutputConversionStep projected => IsStructurallyInspectable(projected.InnerStep),
                KeyedListItemConversionStep keyedList => IsStructurallyInspectable(keyedList.ValueStep),
                NamedConverterStep => false,
                _ => false,
            };
        }
    }
}
