using LiveArch.Deployment.Converters;
using Pulumi;
using System.Collections;
using System.Reflection;
using Type = System.Type;

namespace LiveArch.Deployment
{
    public partial class StructurizrDeploymentProcessor
    {
        /// <summary>
        /// Encapsulates low-level binding of converted values into Pulumi argument objects.
        /// </summary>
        /// <remarks>
        /// The binder resolves input metadata, creates nested input wrappers, and supports plain assignments,
        /// keyed collection writes, and append operations for Pulumi input collection types.
        /// </remarks>
        private sealed class InputValueBinder(StructurizrDeploymentProcessor owner)
        {
            private readonly Dictionary<object, object> childInputWrappers = new();
            private readonly Dictionary<Type, Dictionary<string, PropertyInfo>> allInputProps = new();
            private readonly PropertyInfo inputAttrNameProp = typeof(InputAttribute).GetProperty("Name", BindingFlags.Instance | BindingFlags.NonPublic)!;

            /// <summary>
            /// Builds and caches a lookup of logical Pulumi input names to writable CLR properties.
            /// </summary>
            /// <param name="type">Argument type to inspect.</param>
            /// <returns>A case-insensitive map of input names to target properties.</returns>
            public Dictionary<string, PropertyInfo> GetInputProps(Type type)
            {
                if (allInputProps.TryGetValue(type, out var props))
                {
                    return props;
                }

                props = new Dictionary<string, PropertyInfo>(StringComparer.OrdinalIgnoreCase);

                foreach (var prop in type.GetProperties(BindingFlags.Instance | BindingFlags.Public))
                {
                    var attr = prop.GetCustomAttribute<InputAttribute>();
                    if (attr != null)
                    {
                        var name = (string)inputAttrNameProp.GetValue(attr)!;
                        props[name] = prop;
                    }
                }

                foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.NonPublic))
                {
                    var attr = field.GetCustomAttribute<InputAttribute>();
                    if (attr == null)
                    {
                        continue;
                    }

                    var name = (string)inputAttrNameProp.GetValue(attr)!;
                    var prop = FindPropertyForBackingField(type, field);
                    if (prop != null)
                    {
                        props[name] = prop;
                    }
                }

                allInputProps[type] = props;
                return props;
            }

            /// <summary>
            /// Assigns a value into a plain, nested, or collection-based Pulumi input path.
            /// </summary>
            /// <param name="target">Target argument object.</param>
            /// <param name="path">Input path that may contain nesting, keyed writes, or append syntax.</param>
            /// <param name="value">Raw value to assign.</param>
            /// <param name="inputProps">Cached writable input properties for the current target type.</param>
            /// <param name="context">Current deployment context.</param>
            /// <param name="parseInlineTransformers">Whether inline transformer syntax should be evaluated before conversion.</param>
            /// <param name="converterName">Optional named converter to use during value conversion.</param>
            public void SetProperty(object target, string path, object value, Dictionary<string, PropertyInfo> inputProps, DeploymentContext context, bool parseInlineTransformers = false, string? converterName = null)
            {
                var parts = path.Split('.', 2);

                if (parts.Length == 1)
                {
                    if (parts[0].Contains(':'))
                    {
                        AddKeyToCollection(target, inputProps, parts[0], value, context, parseInlineTransformers, converterName);
                        return;
                    }

                    if (parts[0].Contains("+="))
                    {
                        AddItemsToCollection(target, inputProps, parts[0], value, context, parseInlineTransformers, converterName);
                        return;
                    }

                    if (inputProps.TryGetValue(parts[0], out var prop))
                    {
                        value = owner.PrepareDirectValue(value, context, parseInlineTransformers, out var inlineTransformers);
                        owner.expressionRecorder.RecordDirectAssignment(target, parts[0], value, parseInlineTransformers, converterName, inlineTransformers);
                        var converted = owner.ConvertValue(prop.PropertyType, value, context, converterName);
                        prop.SetValue(target, converted);
                    }

                    return;
                }

                var head = parts[0];
                var tail = parts[1];

                if (!inputProps.TryGetValue(head, out var headProp))
                {
                    return;
                }

                var current = headProp.GetValue(target);
                if (current == null)
                {
                    current = CreateNestedInstance(headProp.PropertyType, out var unwrapped);
                    childInputWrappers[current] = unwrapped ?? current;
                    headProp.SetValue(target, current);
                }

                if (!childInputWrappers.TryGetValue(current, out var nestedTarget))
                {
                    nestedTarget = current;
                    childInputWrappers[current] = nestedTarget;
                }

                owner.expressionRecorder.RegisterNestedTarget(target, nestedTarget, head);

                var nestedProps = GetInputProps(GetUnderlyingArgsType(headProp.PropertyType));
                SetProperty(nestedTarget, tail, value, nestedProps, context, parseInlineTransformers, converterName);
            }

            /// <summary>
            /// Handles append syntax such as <c>property+=...</c> for supported input collections.
            /// </summary>
            private void AddItemsToCollection(object target, Dictionary<string, PropertyInfo> inputProps, string path, object value, DeploymentContext context, bool parseInlineTransformers, string? converterName)
            {
                var parts = path.Split("+=", 2);
                if (parts.Length != 2)
                {
                    throw new InvalidOperationException("Collection append operation requires exactly one '+=' operator");
                }

                var collectionPropName = parts[0];
                if (!inputProps.TryGetValue(collectionPropName, out var collectionProp))
                {
                    throw new InvalidOperationException($"Property {collectionPropName} not found on {target.GetType().Name}");
                }

                var collectionType = collectionProp.PropertyType;
                if (collectionType.IsGenericType && collectionType.GetGenericTypeDefinition() == typeof(InputList<>))
                {
                    AddValuesToList(target, collectionProp, value, context, parseInlineTransformers, converterName);
                    return;
                }

                throw new InvalidOperationException($"Append operation supports only properties of type 'InputList<T>'. '{collectionPropName}' has type '{collectionType.Name}'");
            }

            /// <summary>
            /// Converts and appends values into an <c>InputList&lt;T&gt;</c> property.
            /// </summary>
            private void AddValuesToList(object target, PropertyInfo listProp, object value, DeploymentContext context, bool parseInlineTransformers, string? converterName)
            {
                var listType = listProp.PropertyType;
                var itemType = listType.GetGenericArguments()[0];

                var list = listProp.GetValue(target);
                if (list == null)
                {
                    list = Activator.CreateInstance(listType);
                    listProp.SetValue(target, list);
                }

                var addRangeMethod = listType.GetMethod("AddRange");
                var inputListType = typeof(InputList<>).MakeGenericType(itemType);
                value = owner.PrepareDirectValue(value, context, parseInlineTransformers);
                owner.expressionRecorder.RecordAppendCollectionAssignment(target, listProp.Name, value, parseInlineTransformers, converterName);
                var inputList = owner.ConvertValue(inputListType, value, context, converterName);
                addRangeMethod!.Invoke(list, [inputList]);
            }

            /// <summary>
            /// Handles keyed collection syntax such as <c>property:key</c> for lists and maps.
            /// </summary>
            private void AddKeyToCollection(object target, Dictionary<string, PropertyInfo> inputProps, string path, object value, DeploymentContext context, bool parseInlineTransformers, string? converterName)
            {
                var parts = path.Split(':', 2);
                if (parts.Length != 2)
                {
                    throw new InvalidOperationException("Collection assignment requires exactly one ':' separator");
                }

                var collectionPropName = parts[0];
                var key = parts[1];

                if (!inputProps.TryGetValue(collectionPropName, out var collectionProp))
                {
                    throw new InvalidOperationException($"Property {collectionPropName} not found on {target.GetType().Name}");
                }

                var collectionType = collectionProp.PropertyType;
                if (collectionType.IsGenericType && collectionType.GetGenericTypeDefinition() == typeof(InputList<>))
                {
                    AddKeyToList(target, collectionProp, key, value, context, parseInlineTransformers, converterName);
                    return;
                }

                if (collectionType.IsGenericType && collectionType.GetGenericTypeDefinition() == typeof(InputMap<>))
                {
                    AddKeyToMap(target, collectionProp, key, value, context, parseInlineTransformers, converterName);
                    return;
                }

                throw new InvalidOperationException($"{collectionPropName} is neither InputList<T> nor InputMap<T>");
            }

            /// <summary>
            /// Adds a single keyed item into an <c>InputList&lt;T&gt;</c>, setting the logical item name or key.
            /// </summary>
            private void AddKeyToList(object target, PropertyInfo listProp, string key, object value, DeploymentContext context, bool parseInlineTransformers, string? converterName)
            {
                var listType = listProp.PropertyType;
                var itemType = listType.GetGenericArguments()[0];

                var list = listProp.GetValue(target);
                if (list == null)
                {
                    list = Activator.CreateInstance(listType);
                    listProp.SetValue(target, list);
                }

                var nameProp = (itemType.GetProperty("Name") ?? itemType.GetProperty("Key"))
                    ?? throw new InvalidOperationException($"{itemType.Name} must contain Name or Key property");
                var convertedName = owner.ConvertValue(nameProp.PropertyType, key, context);

                value = owner.PrepareDirectValue(value, context, parseInlineTransformers);

                var item = string.IsNullOrWhiteSpace(converterName)
                    ? owner.ConvertKeyedListItem(itemType, value, context)
                    : owner.ConvertValue(itemType, value, context, converterName);

                owner.expressionRecorder.RecordKeyedCollectionAssignment(target, listProp.Name, key, value, parseInlineTransformers, converterName);

                if (ConversionTypeHelpers.IsOutput(item.GetType()))
                {
                    item = ConversionTypeHelpers.ProjectOutput(
                        item,
                        itemType,
                        itemType,
                        currentItem =>
                        {
                            nameProp.SetValue(currentItem, convertedName);
                            return currentItem;
                        });
                }
                else
                {
                    nameProp.SetValue(item, convertedName);
                }

                var addMethod = listType.GetMethods().Where(m => m.Name == "Add")
                    .Where(m =>
                    {
                        var paramType = m.GetParameters().First().ParameterType;
                        return paramType.IsArray &&
                               paramType.GetElementType()!.IsGenericType &&
                               paramType.GetElementType()!.GetGenericTypeDefinition() == typeof(Input<>);
                    })
                    .Single();

                var inputItemType = typeof(Input<>).MakeGenericType(itemType);
                var inputItem = owner.ConvertValue(inputItemType, item!, context);
                var inputArray = Array.CreateInstance(inputItemType, 1);
                inputArray.SetValue(inputItem, 0);
                addMethod.Invoke(list, [inputArray]);
            }

            /// <summary>
            /// Adds a single keyed value into an <c>InputMap&lt;T&gt;</c> property.
            /// </summary>
            private void AddKeyToMap(object target, PropertyInfo mapProp, string key, object value, DeploymentContext context, bool parseInlineTransformers, string? converterName)
            {
                var mapType = mapProp.PropertyType;
                var valueType = mapType.GetGenericArguments()[0];

                var map = mapProp.GetValue(target);
                if (map == null)
                {
                    map = Activator.CreateInstance(mapType);
                    mapProp.SetValue(target, map);
                }

                var addMethod = mapType.GetMethods()
                    .Where(m => m.Name == "Add" && m.GetParameters().Length == 2)
                    .Single();

                var inputValueType = typeof(Input<>).MakeGenericType(valueType);
                value = owner.PrepareDirectValue(value, context, parseInlineTransformers);
                owner.expressionRecorder.RecordKeyedCollectionAssignment(target, mapProp.Name, key, value, parseInlineTransformers, converterName);
                var convertedValue = owner.ConvertValue(inputValueType, value, context, converterName);
                addMethod.Invoke(map, [key, convertedValue]);
            }

            /// <summary>
            /// Creates a nested input instance and, when needed, exposes the mutable inner object used for recursive binding.
            /// </summary>
            private object CreateNestedInstance(Type type, out object? unwrapped)
            {
                if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Input<>))
                {
                    var inner = type.GetGenericArguments()[0];
                    var instance = Activator.CreateInstance(inner)!;
                    unwrapped = instance;
                    return ConversionTypeHelpers.WrapInput(inner, instance);
                }

                if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(InputList<>))
                {
                    var elem = type.GetGenericArguments()[0];
                    var list = Activator.CreateInstance(typeof(List<>).MakeGenericType(elem))!;
                    unwrapped = list;
                    return ConversionTypeHelpers.WrapInputList(elem, list);
                }

                if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(InputMap<>))
                {
                    var elem = type.GetGenericArguments()[0];
                    var dict = Activator.CreateInstance(typeof(Dictionary<,>).MakeGenericType(typeof(string), elem))!;
                    unwrapped = dict;
                    return ConversionTypeHelpers.WrapInputMap(elem, dict);
                }

                unwrapped = null;
                return Activator.CreateInstance(type)!;
            }

            /// <summary>
            /// Returns the inner argument type for <c>Input&lt;T&gt;</c>, or the original type for non-wrapper types.
            /// </summary>
            private static Type GetUnderlyingArgsType(Type type)
            {
                return type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Input<>)
                    ? type.GetGenericArguments()[0]
                    : type;
            }

            /// <summary>
            /// Tries to map a Pulumi backing field to its corresponding public property.
            /// </summary>
            private static PropertyInfo? FindPropertyForBackingField(Type type, FieldInfo field)
            {
                var name = field.Name;
                if (name.StartsWith('_'))
                {
                    name = name[1..];
                }

                name = char.ToUpper(name[0]) + name[1..];
                return type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public);
            }
        }
    }
}
