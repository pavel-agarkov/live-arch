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
    public sealed class AssignableValueConverter : ITypedValueConverter
    {
        public bool CanConvert(ConversionRequest request)
        {
            return request.TargetType == typeof(object) || request.TargetType.IsAssignableFrom(request.SourceType);
        }

        public object Convert(ConversionRequest request, IConversionEngine engine)
        {
            return request.SourceValue;
        }
    }

    public sealed class ImplicitOperatorValueConverter : ITypedValueConverter
    {
        public bool CanConvert(ConversionRequest request)
        {
            return ConversionTypeHelpers.TryGetImplicitOperator(request.TargetType, request.SourceType, out _);
        }

        public object Convert(ConversionRequest request, IConversionEngine engine)
        {
            if (!ConversionTypeHelpers.TryWrapIntoTargetType(request.TargetType, request.SourceValue, out var wrapped))
            {
                throw new InvalidOperationException($"Cannot apply implicit operator from {request.SourceType.FullName} to {request.TargetType.FullName}");
            }

            return wrapped;
        }
    }

    public sealed class PrimitiveValueConverter : ITypedValueConverter
    {
        private static readonly HashSet<Type> NumericTypes =
        [
            typeof(byte),
            typeof(sbyte),
            typeof(short),
            typeof(ushort),
            typeof(int),
            typeof(uint),
            typeof(long),
            typeof(ulong),
            typeof(float),
            typeof(double),
            typeof(decimal)
        ];

        public bool CanConvert(ConversionRequest request)
        {
            return !ConversionTypeHelpers.IsOutput(request.SourceType) &&
                (request.TargetType == typeof(string) ||
                 request.TargetType == typeof(bool) ||
                 NumericTypes.Contains(request.TargetType) && request.SourceValue is IConvertible);
        }

        public object Convert(ConversionRequest request, IConversionEngine engine)
        {
            if (request.TargetType == typeof(string))
            {
                return request.SourceValue.ToString()!;
            }

            if (request.TargetType == typeof(bool))
            {
                return bool.Parse(request.SourceValue.ToString()!);
            }

            if (NumericTypes.Contains(request.TargetType))
            {
                return System.Convert.ChangeType(request.SourceValue, request.TargetType);
            }

            throw new NotSupportedException($"Cannot convert '{request.SourceValue}' to {request.TargetType}");
        }
    }

    public sealed class PulumiEnumValueConverter : ITypedValueConverter
    {
        public bool CanConvert(ConversionRequest request)
        {
            return !ConversionTypeHelpers.IsOutput(request.SourceType) && ConversionTypeHelpers.IsPulumiEnum(request.TargetType);
        }

        public object Convert(ConversionRequest request, IConversionEngine engine)
        {
            var stringValue = (string)engine.ConvertValue(typeof(string), request.SourceValue, request.Context);
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
    }

    public sealed class UnionValueConverter : ITypedValueConverter
    {
        public bool CanConvert(ConversionRequest request)
        {
            return !ConversionTypeHelpers.IsOutput(request.SourceType) && ConversionTypeHelpers.IsGenericUnion(request.TargetType);
        }

        public object Convert(ConversionRequest request, IConversionEngine engine)
        {
            var unionArgs = request.TargetType.GetGenericArguments();
            for (var i = 0; i < unionArgs.Length; i++)
            {
                if (!TryConvert(engine, unionArgs[i], request, out var converted))
                {
                    continue;
                }

                var fromMethod = request.TargetType.GetMethod($"FromT{i}", BindingFlags.Public | BindingFlags.Static)!;
                return fromMethod.Invoke(null, [converted])!;
            }

            throw new NotSupportedException($"Cannot convert '{request.SourceValue}' to {request.TargetType}");
        }

        private static bool TryConvert(IConversionEngine engine, Type targetType, ConversionRequest request, out object? converted)
        {
            try
            {
                converted = engine.ConvertValue(targetType, request.SourceValue, request.Context);
                return true;
            }
            catch
            {
                converted = null;
                return false;
            }
        }
    }

    public sealed class InputUnionValueConverter : ITypedValueConverter
    {
        public bool CanConvert(ConversionRequest request)
        {
            return !ConversionTypeHelpers.IsOutput(request.SourceType) && ConversionTypeHelpers.IsGenericInputUnion(request.TargetType);
        }

        public object Convert(ConversionRequest request, IConversionEngine engine)
        {
            if (ConversionTypeHelpers.TryWrapIntoTargetType(request.TargetType, request.SourceValue, out var wrapped))
            {
                return wrapped;
            }

            var unionArgs = request.TargetType.GetGenericArguments();
            for (var i = 0; i < unionArgs.Length; i++)
            {
                if (!TryConvert(engine, unionArgs[i], request, out var converted) || converted == null)
                {
                    continue;
                }

                if (ConversionTypeHelpers.TryWrapIntoTargetType(request.TargetType, converted, out wrapped))
                {
                    return wrapped;
                }

                var inputType = typeof(Input<>).MakeGenericType(unionArgs[i]);
                if (TryConvert(engine, inputType, request with { SourceValue = converted }, out var inputValue) &&
                    inputValue != null &&
                    ConversionTypeHelpers.TryWrapIntoTargetType(request.TargetType, inputValue, out wrapped))
                {
                    return wrapped;
                }

                var unionType = typeof(Union<,>).MakeGenericType(unionArgs);
                var fromMethod = unionType.GetMethod($"FromT{i}", BindingFlags.Public | BindingFlags.Static);
                var unionValue = fromMethod?.Invoke(null, [converted]);
                if (unionValue != null && ConversionTypeHelpers.TryWrapIntoTargetType(request.TargetType, unionValue, out wrapped))
                {
                    return wrapped;
                }
            }

            throw new NotSupportedException($"Cannot convert '{request.SourceValue}' to {request.TargetType}");
        }

        private static bool TryConvert(IConversionEngine engine, Type targetType, ConversionRequest request, out object? converted)
        {
            try
            {
                converted = engine.ConvertValue(targetType, request.SourceValue, request.Context);
                return true;
            }
            catch
            {
                converted = null;
                return false;
            }
        }
    }

    public sealed class InputValueConverter : ITypedValueConverter
    {
        public bool CanConvert(ConversionRequest request)
        {
            return !ConversionTypeHelpers.IsOutput(request.SourceType) && ConversionTypeHelpers.IsGenericInput(request.TargetType);
        }

        public object Convert(ConversionRequest request, IConversionEngine engine)
        {
            var innerType = request.TargetType.GetGenericArguments()[0];
            var converted = engine.ConvertValue(innerType, request.SourceValue, request.Context);
            return ConversionTypeHelpers.WrapInput(innerType, converted);
        }
    }

    public sealed class InputListValueConverter : ITypedValueConverter
    {
        public bool CanConvert(ConversionRequest request)
        {
            return !ConversionTypeHelpers.IsOutput(request.SourceType) && ConversionTypeHelpers.IsGenericInputList(request.TargetType);
        }

        public object Convert(ConversionRequest request, IConversionEngine engine)
        {
            var elementType = request.TargetType.GetGenericArguments()[0];
            var typedList = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(elementType))!;

            if (request.SourceValue is IEnumerable enumerable && request.SourceValue is not string)
            {
                foreach (var item in enumerable)
                {
                    typedList.Add(engine.ConvertValue(elementType, item!, request.Context));
                }
            }
            else
            {
                typedList.Add(engine.ConvertValue(elementType, request.SourceValue, request.Context));
            }

            var inputList = Activator.CreateInstance(request.TargetType)!;
            var addMethod = request.TargetType.GetMethods()
                .Where(method => method.Name == "Add")
                .Where(method =>
                {
                    var parameters = method.GetParameters();
                    return parameters.Length == 1 &&
                           parameters[0].ParameterType.IsArray &&
                           parameters[0].ParameterType.GetElementType() is { } elementParameterType &&
                           elementParameterType.IsGenericType &&
                           elementParameterType.GetGenericTypeDefinition() == typeof(Input<>);
                })
                .Single();

            var inputElementType = typeof(Input<>).MakeGenericType(elementType);
            var inputArray = Array.CreateInstance(inputElementType, typedList.Count);
            for (var i = 0; i < typedList.Count; i++)
            {
                inputArray.SetValue(ConversionTypeHelpers.WrapInput(elementType, typedList[i]!), i);
            }

            addMethod.Invoke(inputList, [inputArray]);
            return inputList;
        }
    }

    public sealed class ImmutableArrayValueConverter : ITypedValueConverter
    {
        public bool CanConvert(ConversionRequest request)
        {
            return !ConversionTypeHelpers.IsOutput(request.SourceType) &&
                request.TargetType.IsGenericType &&
                request.TargetType.GetGenericTypeDefinition() == typeof(ImmutableArray<>);
        }

        public object Convert(ConversionRequest request, IConversionEngine engine)
        {
            var elementType = request.TargetType.GetGenericArguments()[0];
            var typedList = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(elementType))!;

            if (request.SourceValue is IEnumerable enumerable && request.SourceValue is not string)
            {
                foreach (var item in enumerable)
                {
                    typedList.Add(engine.ConvertValue(elementType, item!, request.Context));
                }
            }
            else
            {
                typedList.Add(engine.ConvertValue(elementType, request.SourceValue, request.Context));
            }

            var createRange = typeof(ImmutableArray)
                .GetMethods(BindingFlags.Public | BindingFlags.Static)
                .First(method => method.Name == nameof(ImmutableArray.CreateRange) && method.IsGenericMethod && method.GetParameters().Length == 1)
                .MakeGenericMethod(elementType);

            return createRange.Invoke(null, [typedList])!;
        }
    }

    public sealed class ImmutableDictionaryValueConverter : ITypedValueConverter
    {
        public bool CanConvert(ConversionRequest request)
        {
            return !ConversionTypeHelpers.IsOutput(request.SourceType) &&
                request.TargetType.IsGenericType &&
                request.TargetType.GetGenericTypeDefinition() == typeof(ImmutableDictionary<,>) &&
                request.TargetType.GetGenericArguments()[0] == typeof(string) &&
                request.SourceValue is IDictionary;
        }

        public object Convert(ConversionRequest request, IConversionEngine engine)
        {
            var valueType = request.TargetType.GetGenericArguments()[1];
            var typedDictionary = (IDictionary)Activator.CreateInstance(typeof(Dictionary<,>).MakeGenericType(typeof(string), valueType))!;

            foreach (DictionaryEntry entry in (IDictionary)request.SourceValue)
            {
                typedDictionary[entry.Key] = engine.ConvertValue(valueType, entry.Value!, request.Context);
            }

            var createRange = typeof(ImmutableDictionary)
                .GetMethods(BindingFlags.Public | BindingFlags.Static)
                .First(method => method.Name == nameof(ImmutableDictionary.CreateRange) && method.IsGenericMethod && method.GetParameters().Length == 1)
                .MakeGenericMethod(typeof(string), valueType);

            return createRange.Invoke(null, [typedDictionary])!;
        }
    }

    public sealed class ProjectedOutputValueConverter : ITypedValueConverter
    {
        public bool CanConvert(ConversionRequest request)
        {
            return ConversionTypeHelpers.IsOutput(request.SourceType);
        }

        public object Convert(ConversionRequest request, IConversionEngine engine)
        {
            var descriptor = ConversionTypeHelpers.GetOutputProjectionDescriptor(request.TargetType);
            var sourceInnerType = request.SourceType.GetGenericArguments()[0];
            var projected = ConversionTypeHelpers.ProjectOutput(
                request.SourceValue,
                sourceInnerType,
                descriptor.ProjectedTargetType,
                    value => engine.ConvertValue(descriptor.ProjectedTargetType, value, request.Context));

            return descriptor.WrapProjectedOutput(projected);
        }
    }

    public static class KnownNamedValueConverters
    {
        public const string DefaultKeyedListValue = "default-keyed-list-value";
    }

    public sealed class DefaultKeyedListValueConverter : INamedValueConverter
    {
        public string Name => KnownNamedValueConverters.DefaultKeyedListValue;

        public bool CanConvert(ConversionRequest request)
        {
            return !ConversionTypeHelpers.IsOutput(request.SourceType) &&
                request.TargetType.GetProperty("Value", BindingFlags.Public | BindingFlags.Instance) != null;
        }

        public object Convert(ConversionRequest request, IConversionEngine engine)
        {
            var item = Activator.CreateInstance(request.TargetType)
                ?? throw new InvalidOperationException($"Cannot create instance of {request.TargetType.FullName}");

            var valueProperty = request.TargetType.GetProperty("Value", BindingFlags.Public | BindingFlags.Instance)
                ?? throw new InvalidOperationException($"{request.TargetType.Name} must contain Value property");

            var convertedValue = engine.ConvertValue(valueProperty.PropertyType, request.SourceValue, request.Context);
            valueProperty.SetValue(item, convertedValue);

            return item;
        }
    }
}
