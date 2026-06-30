using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reflection;
using Pulumi;
using Type = System.Type;

namespace LiveArch.Deployment.Converters
{
    /// <summary>
    /// Executes resolved conversion plans against runtime values.
    /// </summary>
    public sealed class ConversionPlanExecutor(
        IEnumerable<INamedValueConverter> namedConverters)
    {
        private readonly IReadOnlyDictionary<Type, INamedValueConverter> namedConverterLookup = namedConverters
            .GroupBy(converter => converter.GetType())
            .ToDictionary(group => group.Key, group => group.Single());

        /// <summary>
        /// Executes a previously resolved conversion plan.
        /// </summary>
        /// <param name="plan">Plan to execute.</param>
        /// <param name="request">Original conversion request.</param>
        /// <returns>The converted value.</returns>
        public object Execute(ConversionPlan plan, ConversionRequest request)
        {
            ArgumentNullException.ThrowIfNull(plan);
            return ExecuteStep(plan.RootStep, request);
        }

        private object ExecuteStep(ConversionStep step, ConversionRequest request)
        {
            return step switch
            {
                AssignableConversionStep => request.SourceValue,
                PrimitiveConversionStep => ExecutePrimitive(request),
                PulumiEnumConversionStep => ExecutePulumiEnum(request),
                ImplicitOperatorConversionStep implicitOperatorStep => ExecuteImplicitOperator(implicitOperatorStep, request),
                InputConversionStep inputStep => ExecuteInput(inputStep, request),
                InputListConversionStep inputListStep => ExecuteInputList(inputListStep, request),
                ImmutableArrayConversionStep immutableArrayStep => ExecuteImmutableArray(immutableArrayStep, request),
                ImmutableDictionaryConversionStep immutableDictionaryStep => ExecuteImmutableDictionary(immutableDictionaryStep, request),
                StringEnumUnionConversionStep unionStep => ExecuteStringEnumUnion(unionStep, request),
                StringEnumInputUnionConversionStep inputUnionStep => ExecuteStringEnumInputUnion(inputUnionStep, request),
                ProjectedOutputConversionStep projectedOutputStep => ExecuteProjectedOutput(projectedOutputStep, request),
                NamedConverterStep namedStep => ExecuteNamedConverter(namedStep, request),
                KeyedListItemConversionStep keyedListStep => ExecuteKeyedListItem(keyedListStep, request),
                _ => throw new NotSupportedException($"Unsupported conversion step '{step.GetType().FullName}'."),
            };
        }

        private static object ExecutePrimitive(ConversionRequest request)
        {
            if (request.TargetType == typeof(string))
            {
                return request.SourceValue.ToString()!;
            }

            if (request.TargetType == typeof(bool))
            {
                return bool.Parse(request.SourceValue.ToString()!);
            }

            return Convert.ChangeType(request.SourceValue, request.TargetType);
        }

        private static object ExecutePulumiEnum(ConversionRequest request)
        {
            var stringValue = request.SourceValue.ToString()!;
            var valueField = request.TargetType.GetField("_value", BindingFlags.NonPublic | BindingFlags.Instance);

            foreach (var property in request.TargetType.GetProperties(BindingFlags.Public | BindingFlags.Static))
            {
                var enumValue = property.GetValue(null)!;
                var enumString = valueField?.GetValue(enumValue)?.ToString();
                if (string.Equals(enumString, stringValue, StringComparison.OrdinalIgnoreCase))
                {
                    return enumValue;
                }
            }

            throw new NotSupportedException($"Cannot convert '{request.SourceValue}' to Pulumi enum type {request.TargetType.Name}");
        }

        private static object ExecuteImplicitOperator(ImplicitOperatorConversionStep step, ConversionRequest request)
        {
            if (!ConversionTypeHelpers.TryWrapIntoTargetType(step.TargetType, request.SourceValue, out var wrapped))
            {
                throw new InvalidOperationException($"Cannot apply implicit operator from {request.SourceType.FullName} to {step.TargetType.FullName}");
            }

            return wrapped;
        }

        private object ExecuteInput(InputConversionStep step, ConversionRequest request)
        {
            var converted = ExecuteNestedStep(step.InnerStep, step.InnerType, request.SourceValue);
            return ConversionTypeHelpers.WrapInput(step.InnerType, converted);
        }

        private object ExecuteInputList(InputListConversionStep step, ConversionRequest request)
        {
            var typedList = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(step.ElementType))!;

            foreach (var item in EnumerateOrSingle(request.SourceValue))
            {
                typedList.Add(ExecuteNestedStep(step.ElementStep, step.ElementType, item!));
            }

            return ConversionTypeHelpers.WrapInputList(step.ElementType, typedList);
        }

        private object ExecuteImmutableArray(ImmutableArrayConversionStep step, ConversionRequest request)
        {
            var typedList = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(step.ElementType))!;

            foreach (var item in EnumerateOrSingle(request.SourceValue))
            {
                typedList.Add(ExecuteNestedStep(step.ElementStep, step.ElementType, item!));
            }

            var createRange = typeof(ImmutableArray)
                .GetMethods(BindingFlags.Public | BindingFlags.Static)
                .First(method => method.Name == nameof(ImmutableArray.CreateRange) && method.IsGenericMethod && method.GetParameters().Length == 1)
                .MakeGenericMethod(step.ElementType);

            return createRange.Invoke(null, [typedList])!;
        }

        private object ExecuteImmutableDictionary(ImmutableDictionaryConversionStep step, ConversionRequest request)
        {
            var typedDictionary = (IDictionary)Activator.CreateInstance(typeof(Dictionary<,>).MakeGenericType(typeof(string), step.ValueType))!;

            foreach (DictionaryEntry entry in (IDictionary)request.SourceValue)
            {
                typedDictionary[entry.Key] = ExecuteNestedStep(step.ValueStep, step.ValueType, entry.Value!);
            }

            var createRange = typeof(ImmutableDictionary)
                .GetMethods(BindingFlags.Public | BindingFlags.Static)
                .First(method => method.Name == nameof(ImmutableDictionary.CreateRange) && method.IsGenericMethod && method.GetParameters().Length == 1)
                .MakeGenericMethod(typeof(string), step.ValueType);

            return createRange.Invoke(null, [typedDictionary])!;
        }

        private object ExecuteStringEnumUnion(StringEnumUnionConversionStep step, ConversionRequest request)
        {
            // Try string first (T0)
            if (request.SourceValue is string)
            {
                var fromT0 = request.TargetType.GetMethod("FromT0", BindingFlags.Public | BindingFlags.Static)!;
                return fromT0.Invoke(null, [request.SourceValue])!;
            }

            // Try enum conversion (T1)
            if (TryExecuteNestedStep(step.EnumStep, step.EnumType, request.SourceValue, out var enumConverted))
            {
                var fromT1 = request.TargetType.GetMethod("FromT1", BindingFlags.Public | BindingFlags.Static)!;
                return fromT1.Invoke(null, [enumConverted])!;
            }

            throw new NotSupportedException($"Cannot convert '{request.SourceValue}' to {request.TargetType}");
        }

        private object ExecuteStringEnumInputUnion(StringEnumInputUnionConversionStep step, ConversionRequest request)
        {
            // First try direct implicit wrapping
            if (ConversionTypeHelpers.TryWrapIntoTargetType(request.TargetType, request.SourceValue, out var wrapped))
            {
                return wrapped;
            }

            // Try string branch (T0)
            if (request.SourceValue is string stringValue)
            {
                if (ConversionTypeHelpers.TryWrapIntoTargetType(request.TargetType, stringValue, out wrapped))
                {
                    return wrapped;
                }

                var inputString = ConversionTypeHelpers.WrapInput(typeof(string), stringValue);
                if (ConversionTypeHelpers.TryWrapIntoTargetType(request.TargetType, inputString, out wrapped))
                {
                    return wrapped;
                }

                var unionType = typeof(Union<,>).MakeGenericType(typeof(string), step.EnumType);
                var fromT0 = unionType.GetMethod("FromT0", BindingFlags.Public | BindingFlags.Static);
                var unionValue = fromT0?.Invoke(null, [stringValue]);
                if (unionValue != null && ConversionTypeHelpers.TryWrapIntoTargetType(request.TargetType, unionValue, out wrapped))
                {
                    return wrapped;
                }
            }

            // Try enum branch (T1)
            if (TryExecuteNestedStep(step.EnumStep, step.EnumType, request.SourceValue, out var enumConverted) && enumConverted != null)
            {
                if (ConversionTypeHelpers.TryWrapIntoTargetType(request.TargetType, enumConverted, out wrapped))
                {
                    return wrapped;
                }

                var inputEnum = ConversionTypeHelpers.WrapInput(step.EnumType, enumConverted);
                if (ConversionTypeHelpers.TryWrapIntoTargetType(request.TargetType, inputEnum, out wrapped))
                {
                    return wrapped;
                }

                var unionType = typeof(Union<,>).MakeGenericType(typeof(string), step.EnumType);
                var fromT1 = unionType.GetMethod("FromT1", BindingFlags.Public | BindingFlags.Static);
                var unionValue = fromT1?.Invoke(null, [enumConverted]);
                if (unionValue != null && ConversionTypeHelpers.TryWrapIntoTargetType(request.TargetType, unionValue, out wrapped))
                {
                    return wrapped;
                }
            }

            throw new NotSupportedException($"Cannot convert '{request.SourceValue}' to {request.TargetType}");
        }

        private object ExecuteProjectedOutput(ProjectedOutputConversionStep step, ConversionRequest request)
        {
            var descriptor = ConversionTypeHelpers.GetOutputProjectionDescriptor(request.TargetType);
            var sourceInnerType = request.SourceType.GetGenericArguments()[0];
            var projected = ConversionTypeHelpers.ProjectOutput(
                request.SourceValue,
                sourceInnerType,
                step.ProjectedTargetType,
                value => ExecuteNestedStep(step.InnerStep, step.ProjectedTargetType, value));

            return descriptor.WrapProjectedOutput(projected);
        }

        private object ExecuteNamedConverter(NamedConverterStep step, ConversionRequest request)
        {
            if (!namedConverterLookup.TryGetValue(step.ConverterImplementationType, out var converter))
            {
                throw new InvalidOperationException($"Named converter '{step.ConverterImplementationType.FullName}' is not registered.");
            }

            return converter.Convert(request);
        }

        private object ExecuteKeyedListItem(KeyedListItemConversionStep step, ConversionRequest request)
        {
            var item = Activator.CreateInstance(step.ItemType)
                ?? throw new InvalidOperationException($"Cannot create instance of {step.ItemType.FullName}");

            var valueProperty = step.ItemType.GetProperty("Value", BindingFlags.Public | BindingFlags.Instance)
                ?? throw new InvalidOperationException($"{step.ItemType.Name} must contain a public Value property");

            var convertedValue = ExecuteNestedStep(step.ValueStep, step.ValuePropertyType, request.SourceValue);
            valueProperty.SetValue(item, convertedValue);

            return item;
        }

        private object ExecuteNestedStep(ConversionStep step, Type targetType, object sourceValue)
        {
            var request = new ConversionRequest(targetType, sourceValue);
            return ExecuteStep(step, request);
        }

        private bool TryExecuteNestedStep(ConversionStep step, Type targetType, object sourceValue, out object? converted)
        {
            try
            {
                converted = ExecuteNestedStep(step, targetType, sourceValue);
                return true;
            }
            catch
            {
                converted = null;
                return false;
            }
        }

        private static IEnumerable<object?> EnumerateOrSingle(object sourceValue)
        {
            if (sourceValue is IEnumerable enumerable && sourceValue is not string)
            {
                foreach (var item in enumerable)
                {
                    yield return item;
                }

                yield break;
            }

            yield return sourceValue;
        }
    }
}
