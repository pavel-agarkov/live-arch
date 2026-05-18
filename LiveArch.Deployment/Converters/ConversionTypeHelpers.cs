using Pulumi;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reflection;
using Type = System.Type;

namespace LiveArch.Deployment.Converters
{
    public readonly record struct OutputProjectionDescriptor(Type ProjectedTargetType, Func<object, object> WrapProjectedOutput);

    public static class ConversionTypeHelpers
    {
        public static bool IsOutput(Type type)
        {
            return type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Output<>);
        }

        public static bool IsGenericInput(Type type)
        {
            return type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Input<>);
        }

        public static bool IsGenericInputList(Type type)
        {
            return type.IsGenericType && type.GetGenericTypeDefinition() == typeof(InputList<>);
        }

        public static bool IsGenericInputMap(Type type)
        {
            return type.IsGenericType && type.GetGenericTypeDefinition() == typeof(InputMap<>);
        }

        public static bool IsGenericInputUnion(Type type)
        {
            return type.IsGenericType && type.GetGenericTypeDefinition() == typeof(InputUnion<,>);
        }

        public static bool IsGenericUnion(Type type)
        {
            return type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Union<,>);
        }

        public static bool IsPulumiEnum(Type type)
        {
            return type.GetCustomAttribute<EnumTypeAttribute>() != null;
        }

        public static bool TryGetImplicitOperator(Type targetType, Type sourceType, out MethodInfo? implicitOperator)
        {
            var candidateOperators = targetType.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Where(method => method.Name == "op_Implicit" && method.ReturnType == targetType)
                .Where(method => method.GetParameters().Length == 1)
                .Cast<MethodBase>()
                .ToArray();

            if (candidateOperators.Length == 0)
            {
                implicitOperator = null;
                return false;
            }

            implicitOperator = Type.DefaultBinder.SelectMethod(
                BindingFlags.Public | BindingFlags.Static,
                candidateOperators,
                [sourceType],
                modifiers: null) as MethodInfo;

            return implicitOperator != null;
        }

        public static bool TryWrapIntoTargetType(Type targetType, object value, out object wrapped)
        {
            if (TryGetImplicitOperator(targetType, value.GetType(), out var implicitOperator))
            {
                wrapped = implicitOperator!.Invoke(null, [value])!;
                return true;
            }

            wrapped = null!;
            return false;
        }

        public static object WrapIntoTargetType(Type targetType, Type parameterType, object value)
        {
            var implicitOperator = targetType.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .SingleOrDefault(method =>
                {
                    if (method.Name != "op_Implicit" || method.ReturnType != targetType)
                    {
                        return false;
                    }

                    var parameters = method.GetParameters();
                    return parameters.Length == 1 && parameters[0].ParameterType == parameterType;
                });

            if (implicitOperator == null)
            {
                throw new InvalidOperationException($"Cannot find implicit operator from {parameterType.FullName} to {targetType.FullName}");
            }

            return implicitOperator.Invoke(null, [value])!;
        }

        public static object ConvertOutputToInput(Type innerType, object output)
        {
            var inputType = typeof(Input<>).MakeGenericType(innerType);
            if (!TryWrapIntoTargetType(inputType, output, out var wrapped))
            {
                throw new InvalidOperationException($"Cannot convert {output.GetType().FullName} to {inputType.FullName}");
            }

            return wrapped;
        }

        public static object WrapInput(Type innerType, object value)
        {
            var inputType = typeof(Input<>).MakeGenericType(innerType);
            if (TryWrapIntoTargetType(inputType, value, out var wrapped))
            {
                return wrapped;
            }

            var output = WrapOutput(innerType, value);
            if (TryWrapIntoTargetType(inputType, output, out wrapped))
            {
                return wrapped;
            }

            throw new InvalidOperationException($"Cannot wrap value into Input<{innerType.Name}>");
        }

        public static object WrapOutput(Type innerType, object value)
        {
            var createMethod = typeof(Output)
                .GetMethods(BindingFlags.Public | BindingFlags.Static)
                .First(method => method.Name == "Create" && method.IsGenericMethod)
                .MakeGenericMethod(innerType);

            return createMethod.Invoke(null, [value])!;
        }

        public static object WrapInputList(Type elementType, object listObj)
        {
            var listType = typeof(List<>).MakeGenericType(elementType);
            if (!listType.IsInstanceOfType(listObj))
            {
                var typedList = (IList)Activator.CreateInstance(listType)!;
                foreach (var item in (IEnumerable)listObj)
                {
                    typedList.Add(item);
                }

                listObj = typedList;
            }

            if (!TryWrapIntoTargetType(typeof(InputList<>).MakeGenericType(elementType), listObj, out var wrapped))
            {
                throw new InvalidOperationException($"Cannot wrap value into InputList<{elementType.Name}>");
            }

            return wrapped;
        }

        public static object WrapInputMap(Type valueType, object dictionaryObj)
        {
            var dictionaryType = typeof(Dictionary<,>).MakeGenericType(typeof(string), valueType);
            if (!dictionaryType.IsInstanceOfType(dictionaryObj))
            {
                var typedDictionary = (IDictionary)Activator.CreateInstance(dictionaryType)!;
                foreach (DictionaryEntry entry in (IDictionary)dictionaryObj)
                {
                    typedDictionary[entry.Key] = entry.Value;
                }

                dictionaryObj = typedDictionary;
            }

            if (!TryWrapIntoTargetType(typeof(InputMap<>).MakeGenericType(valueType), dictionaryObj, out var wrapped))
            {
                throw new InvalidOperationException($"Cannot wrap value into InputMap<{valueType.Name}>");
            }

            return wrapped;
        }

        public static OutputProjectionDescriptor GetOutputProjectionDescriptor(Type targetType)
        {
            if (IsGenericInput(targetType))
            {
                var innerType = targetType.GetGenericArguments()[0];
                return new OutputProjectionDescriptor(innerType, projectedOutput => ConvertOutputToInput(innerType, projectedOutput));
            }

            if (IsGenericInputUnion(targetType))
            {
                var unionType = typeof(Union<,>).MakeGenericType(targetType.GetGenericArguments());
                return new OutputProjectionDescriptor(unionType, projectedOutput =>
                {
                    if (!TryWrapIntoTargetType(targetType, projectedOutput, out var wrapped))
                    {
                        throw new InvalidOperationException($"Cannot wrap projected output into {targetType.FullName}");
                    }

                    return wrapped;
                });
            }

            if (IsGenericInputList(targetType))
            {
                var elementType = targetType.GetGenericArguments()[0];
                var immutableArrayType = typeof(ImmutableArray<>).MakeGenericType(elementType);
                var outputImmutableArrayType = typeof(Output<>).MakeGenericType(immutableArrayType);
                return new OutputProjectionDescriptor(immutableArrayType, projectedOutput =>
                {
                    return WrapIntoTargetType(targetType, outputImmutableArrayType, projectedOutput);
                });
            }

            if (IsGenericInputMap(targetType))
            {
                var valueType = targetType.GetGenericArguments()[0];
                var immutableDictionaryType = typeof(ImmutableDictionary<,>).MakeGenericType(typeof(string), valueType);
                var outputImmutableDictionaryType = typeof(Output<>).MakeGenericType(immutableDictionaryType);
                return new OutputProjectionDescriptor(immutableDictionaryType, projectedOutput =>
                {
                    return WrapIntoTargetType(targetType, outputImmutableDictionaryType, projectedOutput);
                });
            }

            return new OutputProjectionDescriptor(targetType, static projectedOutput => projectedOutput);
        }

        public static object ProjectOutput(object outputObj, Type sourceInnerType, Type resultType, Func<object, object> projector)
        {
            var helperMethod = typeof(ConversionTypeHelpers)
                .GetMethod(nameof(ProjectOutputCore), BindingFlags.NonPublic | BindingFlags.Static)!
                .MakeGenericMethod(sourceInnerType, resultType);

            return helperMethod.Invoke(null, [outputObj, projector])!;
        }

        private static Output<TResult> ProjectOutputCore<TSource, TResult>(Output<TSource> output, Func<object, object> projector)
        {
            return output.Apply(value => (TResult)projector(value!));
        }

    }
}
