using Pulumi;
using Pulumi.AzureNative.Resources;
using Pulumi.AzureNative.Web;
using Pulumi.AzureNative.Web.Inputs;
using Pulumi.DockerBuild;
using Structurizr;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace LiveArch.Deployment
{
    public class StructurizrComponent
    {
        private readonly string environment;
        private Workspace workspace;
        private Dictionary<string, Type> resourceTypes;
        private Dictionary<string, MethodInfo> invokeMethods = new();
        private Dictionary<Element, object> newResources = new();
        private Dictionary<Element, object> oldResources = new();
        private Dictionary<object, object> childInputWrappers = new();

        private Dictionary<Type, Dictionary<string, PropertyInfo>> allInputProps = new();
        private readonly Dictionary<Type, Dictionary<string, MemberInfo>> _outputMembersCache = new();

        private PropertyInfo inputAttrNameProp;
        private readonly InvokeOptions? invokeOptions = null;
        private readonly CustomResourceOptions? customResourceOptions = null;


        public StructurizrComponent(string workspacePath, string environment)
        {
            var json = new FileInfo(workspacePath);
            workspace = WorkspaceUtils.LoadWorkspaceFromJson(json);
            this.environment = environment;
            CachePulumiTypes();
        }

        public async Task ProcessWorkspaceAsync(CancellationToken cancellationToken)
        {
            foreach (var deployNode in workspace.Model.DeploymentNodes.On(environment))
            {
                await ProcessDeploymentNodeAsync(deployNode, cancellationToken);
            }
        }

        private void CachePulumiTypes()
        {
            inputAttrNameProp = typeof(InputAttribute).GetProperty("Name", BindingFlags.Instance | BindingFlags.NonPublic)!;

            var pulumiTypes = typeof(ResourceGroup).Assembly.GetTypes();
            resourceTypes = pulumiTypes
                .Select(t =>
                {
                    var attr = t.GetCustomAttribute<ResourceTypeAttribute>(true);
                    return new { t, attr?.Type };
                })
                .Where(x => x.Type != null)
                .ToDictionary(x => x.Type!, x => x.t);

            var dockerTypes = typeof(Image).Assembly.GetTypes();
            foreach (var dockerResType in dockerTypes)
            {
                var attr = dockerResType.GetCustomAttribute<ResourceTypeAttribute>(true);
                if (attr != null)
                {
                    resourceTypes.Add(attr.Type, dockerResType);
                }
            }

            foreach (var type in pulumiTypes)
            {
                if (!type.IsAbstract || !type.IsSealed) continue;
                if (!type.Name.StartsWith("Get")) continue;

                var invokeAsync = type.GetMethods(BindingFlags.Public | BindingFlags.Static)
                    .FirstOrDefault(m => m.Name == "InvokeAsync");

                if (invokeAsync == null) continue;

                var token = ExtractInvokeToken(invokeAsync);
                if (token == null) continue;

                invokeMethods[token] = invokeAsync;
                resourceTypes[token] = type;
            }
        }

        private static string? ExtractInvokeToken(MethodInfo method)
        {
            // Ищем вызов Deployment.Instance.InvokeAsync<T>(token, ...)
            var body = method.GetMethodBody();
            if (body == null) return null;

            // Ищем строковые литералы в IL
            var module = method.Module;
            var il = body.GetILAsByteArray();

            for (int i = 0; i < il.Length - 1; i++)
            {
                // ldstr = 0x72
                if (il[i] == 0x72)
                {
                    int metadataToken = BitConverter.ToInt32(il, i + 1);
                    var str = module.ResolveString(metadataToken);

                    // Ищем строки вида "azure-native:keyvault:getVault"
                    if (str.Contains(":") && str.Count(c => c == ':') >= 2)
                        return str;
                }
            }

            return null;
        }

        protected async Task ProcessDeploymentNodeAsync(DeploymentNode deployNode, CancellationToken cancellationToken)
        {
            await CreateNodeAsync(new DeploymentNodeAdapter(deployNode), cancellationToken);

            foreach (var infraNode in deployNode.InfrastructureNodes.On(environment))
            {
                await ProcessInfrastructureNodeAsync(infraNode, cancellationToken);
            }

            foreach (var containerInstance in deployNode.ContainerInstances.On(environment))
            {
                await ProcessContainerInstanceAsync(containerInstance!, cancellationToken);
            }


            foreach (var childNode in deployNode.Children)
            {
                await ProcessDeploymentNodeAsync(childNode!, cancellationToken);
            }

        }

        private async Task ProcessInfrastructureNodeAsync(InfrastructureNode infraNode, CancellationToken cancellationToken)
        {
            await CreateNodeAsync(new InfrastructureNodeAdapter(infraNode), cancellationToken);
        }

        private async Task ProcessContainerInstanceAsync(ContainerInstance containerInstance, CancellationToken cancellationToken)
        {
            await BuildContainerInstance(containerInstance, cancellationToken);
            await CreateNodeAsync(new ContainerInstanceAdapter(containerInstance), cancellationToken);
        }

        private async Task BuildContainerInstance(ContainerInstance containerInstance, CancellationToken cancellationToken)
        {
            await CreateNodeAsync(new ContainerBuildAdapter(containerInstance.Container), cancellationToken);
        }

        private async Task CreateNodeAsync(IDeploymentNode deployNode, CancellationToken cancellationToken)
        {
            if (resourceTypes.TryGetValue(deployNode.Technology, out var type))
            {
                if (type.IsAbstract && type.IsSealed)
                {
                    if (invokeMethods.TryGetValue(deployNode.Technology, out var invoke))
                    {
                        var paramType = invoke.GetParameters().First();
                        var param = Activator.CreateInstance(paramType.ParameterType)!;
                        var paramInputProps = GetInputProps(paramType.ParameterType);

                        if (deployNode.Parent != null)
                        {
                            PropagateParentProperties(deployNode.Parent, param, paramInputProps);
                        }

                        ApplyRelations(deployNode, param);

                        foreach ((var propName, var propVal) in deployNode.Properties)
                        {
                            SetProperty(param, propName, propVal, paramInputProps);
                        }


                        var task = (Task)invoke.Invoke(null, [param, invokeOptions!])!;
                        await task.ConfigureAwait(false);

                        var resultProperty = task.GetType().GetProperty("Result");
                        var resource = resultProperty!.GetValue(task);


                        oldResources.Add(deployNode.Node, resource!);
                    }
                }
                else
                {
                    var paramType = type.GetConstructors()[0].GetParameters()[1];
                    var param = Activator.CreateInstance(paramType.ParameterType)!;
                    var paramInputProps = GetInputProps(paramType.ParameterType);

                    if (deployNode.Parent != null)
                    {
                        PropagateParentProperties(deployNode.Parent, param, paramInputProps);
                    }

                    ApplyRelations(deployNode, param);

                    foreach ((var propName, var propVal) in deployNode.Properties)
                    {
                        SetProperty(param, propName, propVal, paramInputProps);
                    }

                    if (!deployNode.Properties.TryGetValue("var", out var resVar) &&
                        (!deployNode.Properties.TryGetValue("structurizr.dsl.identifier", out resVar) || Guid.TryParse(resVar, out _)) &&
                        !deployNode.Properties.TryGetValue("name", out resVar))
                    {
                        resVar = deployNode.Name;
                    }

                    var newRes = Activator.CreateInstance(type, [resVar, param, customResourceOptions!]);
                    newResources.Add(deployNode.Node, newRes!);
                }
            }

        }

        private void ApplyRelations(IDeploymentNode deployNode, object param)
        {
            foreach (var relation in deployNode.Relationships)
            {
                if (oldResources.TryGetValue(relation.Destination, out var source) || newResources.TryGetValue(relation.Destination, out source))
                {
                    if (relation.Properties.TryGetValue("source", out var sourcePath) && relation.Properties.TryGetValue("target", out var targetPath))
                    {
                        ApplyDependency(source, param, sourcePath, targetPath);
                    }
                }
            }

            if (deployNode.Node is ContainerInstance ci && newResources.TryGetValue(ci.Container, out var image) && image is Image dockerImage && param is WebAppArgs web)
            {
                SetProperty(web, "siteConfig.linuxFxVersion", Output.Format($"DOCKER|{dockerImage.Ref}"), GetInputProps(typeof(WebAppArgs)));
            }
        }

        private void PropagateParentProperties(IDeploymentNode deployNode, object param, Dictionary<string, PropertyInfo> paramInputProps)
        {
            if (deployNode.Parent != null)
            {
                PropagateParentProperties(deployNode.Parent, param, paramInputProps);
            }
            if (!oldResources.TryGetValue(deployNode.Node, out object? resource) &&
                !newResources.TryGetValue(deployNode.Node, out resource))
            {
                if (deployNode.Node is SoftwareSystem)
                {
                    return;
                }
                throw new InvalidOperationException("Resource has not been created yet");
            }

            if (ResourceHierarchy.Registry.TryGetValue(resource.GetType(), out var rules))
            {
                foreach (var rule in rules)
                {
                    var value = rule.ParentOutputProperty(resource);
                    if (value != null)
                    {
                        foreach (var targetProp in rule.TargetInputProperties)
                        {
                            SetProperty(param, targetProp, value, paramInputProps);
                        }
                    }
                }
            }
        }

        private Dictionary<string, PropertyInfo> GetInputProps(Type type)
        {
            if (allInputProps.TryGetValue(type, out var props))
            {
                return props;
            }

            props = new Dictionary<string, PropertyInfo>(StringComparer.OrdinalIgnoreCase);

            // 1. public props
            foreach (var prop in type.GetProperties(BindingFlags.Instance | BindingFlags.Public))
            {
                var attr = prop.GetCustomAttribute<InputAttribute>();
                if (attr != null)
                {
                    var name = (string)inputAttrNameProp.GetValue(attr)!;
                    props[name] = prop;
                }
            }

            // 2. private fields
            foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.NonPublic))
            {
                var attr = field.GetCustomAttribute<InputAttribute>();
                if (attr == null) continue;

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

        private Dictionary<string, MemberInfo> GetOutputMembers(Type type)
        {
            if (_outputMembersCache.TryGetValue(type, out var cached))
                return cached;

            var dict = new Dictionary<string, MemberInfo>(StringComparer.OrdinalIgnoreCase);

            // 1. CustomResource с [Output]
            foreach (var prop in type.GetProperties(BindingFlags.Instance | BindingFlags.Public))
            {
                var outAttr = prop.GetCustomAttribute<OutputAttribute>();
                if (outAttr != null)
                {
                    var name = outAttr.Name; // "name", "numberOfSites" и т.п.
                    dict[name] = prop;
                }
            }

            // 2. [OutputType] – поля/свойства → camelCase
            var outputTypeAttr = type.GetCustomAttribute<OutputTypeAttribute>();
            if (outputTypeAttr != null)
            {
                foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.Public))
                {
                    var name = ToCamelCase(field.Name); // Name → name, ResourceGroup → resourceGroup
                    dict[name] = field;
                }

                foreach (var prop in type.GetProperties(BindingFlags.Instance | BindingFlags.Public))
                {
                    var name = ToCamelCase(prop.Name);
                    dict[name] = prop;
                }
            }

            _outputMembersCache[type] = dict;
            return dict;
        }

        private static string ToCamelCase(string name)
        {
            if (string.IsNullOrEmpty(name) || char.IsLower(name[0]))
                return name;
            return char.ToLowerInvariant(name[0]) + name.Substring(1);
        }

        // source – уже созданный Pulumi ресурс или результат Get* (OutputType)
        // path – "name", "policy.objectId", "permissions.secrets"
        public object? GetOutputValue(object source, string path)
        {
            var parts = path.Split('.', 2);
            var head = parts[0];
            var tail = parts.Length > 1 ? parts[1] : null;

            var type = source.GetType();
            var members = GetOutputMembers(type);

            if (!members.TryGetValue(head, out var member))
                return null;

            object? value = member switch
            {
                PropertyInfo p => p.GetValue(source),
                FieldInfo f => f.GetValue(source),
                _ => null
            };

            if (value == null)
            {
                return null;
            }

            // если это Output<T> – дальше работаем с T
            var valueType = value.GetType();
            if (IsOutput(valueType))
            {
                // тут у тебя уже есть своя логика работы с Output<T> (Apply и т.п.)
                // для маппинга зависимостей обычно достаточно сохранить сам Output<T>
                // и передать его в ConvertValue при установке target
                if (tail == null)
                {
                    return value;
                }

                // вложенный путь внутри OutputType – нужно Apply
                // Output<TOuter> → Output<TInner>
                var innerType = valueType.GetGenericArguments()[0];
                return ProjectNestedOutput(value, innerType, tail);
            }

            // если нет хвоста – это конечное значение
            if (tail == null)
            {
                return value;
            }

            // вложенный объект – рекурсивно
            return GetOutputValue(value, tail);
        }

        private object ProjectNestedOutput(object outputObj, Type innerType, string tailPath)
        {
            // Output<TInner>.Apply(x => GetOutputValue(x, tailPath))
            var outputType = typeof(Output<>).MakeGenericType(innerType);
            var applyMethod = outputType.GetMethods()
                .First(m => m.Name == "Apply" && m.GetParameters().Length == 1);

            // Func<TInner, object?>
            var funcType = typeof(Func<,>).MakeGenericType(innerType, typeof(object));
            //var func = (Delegate)Activator.CreateInstance(
            //    typeof(Func<,>).MakeGenericType(innerType, typeof(object)),
            //    (object?)(TInner x) => GetOutputValue(x!, tailPath))!; // псевдокод, можно собрать через Expression

            //return applyMethod.Invoke(outputObj, new object[] { func })!;

            return null!;
        }

        private static bool IsOutput(Type t)
            => t.IsGenericType && t.GetGenericTypeDefinition() == typeof(Output<>);

        public void ApplyDependency(object source, object target, string sourcePath, string targetPath)
        {
            var value = GetOutputValue(source, sourcePath);
            if (value == null)
                return;

            var inputProps = GetInputProps(target.GetType());
            SetProperty(target, targetPath, value, inputProps);
        }

        private static PropertyInfo? FindPropertyForBackingField(Type type, FieldInfo field)
        {
            var name = field.Name;

            if (name.StartsWith('_'))
            {
                name = name[1..];
            }

            // PascalCase
            name = char.ToUpper(name[0]) + name[1..];

            return type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public);
        }

        public void SetProperty(object target, string path, object value, Dictionary<string, PropertyInfo> inputProps)
        {
            var parts = path.Split('.', 2);

            if (parts.Length == 1)
            {
                // leaf property
                if (inputProps.TryGetValue(parts[0], out var prop))
                {
                    object converted = ConvertValue(prop.PropertyType, value);
                    prop.SetValue(target, converted);
                }
                return;
            }

            var head = parts[0];
            var tail = parts[1];

            if (!inputProps.TryGetValue(head, out var headProp))
                return;

            var current = headProp.GetValue(target);
            if (current == null)
            {
                current = CreateNestedInstance(headProp.PropertyType, out var unwrapped);
                childInputWrappers[current] = unwrapped ?? current;
                headProp.SetValue(target, current);
            }

            var nestedProps = GetInputProps(GetUnderlyingArgsType(headProp.PropertyType));

            SetProperty(childInputWrappers[current], tail, value, nestedProps);
        }

        private static object CreateNestedInstance(Type type, out object? unwrapped)
        {
            // Input<T>
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Input<>))
            {
                var inner = type.GetGenericArguments()[0];
                var instance = Activator.CreateInstance(inner)!;
                unwrapped = instance;
                return WrapInput(inner, instance);
            }

            // InputList<T>
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(InputList<>))
            {
                var elem = type.GetGenericArguments()[0];
                var list = Activator.CreateInstance(typeof(List<>).MakeGenericType(elem))!;
                unwrapped = list;
                return WrapInputList(elem, list);
            }

            // InputMap<T>
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(InputMap<>))
            {
                var elem = type.GetGenericArguments()[0];
                var dict = Activator.CreateInstance(typeof(Dictionary<,>)
                    .MakeGenericType(typeof(string), elem))!;
                unwrapped = dict;
                return WrapInputMap(elem, dict);
            }

            // zwykły Args
            unwrapped = null;
            return Activator.CreateInstance(type)!;
        }

        private static Type GetUnderlyingArgsType(Type type)
        {
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Input<>))
            {
                return type.GetGenericArguments()[0];
            }

            return type;
        }

        public static object ConvertValue(Type targetType, object rawValue)
        {
            if (rawValue == null)
                return null!;

            var rawType = rawValue.GetType();

            // source value is Output
            if (IsGenericOutput(rawType))
            {
                var innerTargetType = targetType.GetGenericArguments()[0]!;

                // target is Input
                if (IsGenericInput(targetType))
                {
                    CheckGenericArguments(targetType, rawType, innerTargetType);
                    return ConvertOutputToInput(innerTargetType, rawValue);
                }

                if (IsGenericInputList(targetType))
                {
                    CheckGenericArguments(targetType, rawType, innerTargetType);
                    return ConvertOutputToInputList(innerTargetType, rawValue);
                }

                throw new NotSupportedException($"Cannot convert {rawType.FullName} to {targetType.FullName}");
            }

            // Если значение уже подходит — возвращаем как есть
            if (targetType.IsAssignableFrom(rawType))
                return rawValue;

            // Input<T>
            if (IsGenericInput(targetType))
            {
                var innerType = targetType.GetGenericArguments()[0];

                // если rawValue уже совместим с innerType → используем implicit operator
                if (innerType.IsAssignableFrom(rawType))
                    return WrapInput(innerType, rawValue);

                // иначе конвертируем и оборачиваем
                var converted = ConvertValue(innerType, rawValue);
                return WrapInput(innerType, converted);
            }

            // InputList<T>
            if (targetType.IsGenericType && targetType.GetGenericTypeDefinition() == typeof(InputList<>))
            {
                var elemType = targetType.GetGenericArguments()[0];
                var list = ConvertToList(elemType, rawValue);
                return WrapInputList(elemType, list);
            }

            // InputMap<T>
            if (targetType.IsGenericType && targetType.GetGenericTypeDefinition() == typeof(InputMap<>))
            {
                var elemType = targetType.GetGenericArguments()[0];
                var dict = ConvertToDictionary(elemType, rawValue);
                return WrapInputMap(elemType, dict);
            }

            // Union<T0,T1>
            if (targetType.IsGenericType &&
                targetType.GetGenericTypeDefinition() == typeof(Union<,>))
            {
                return ConvertToUnion(targetType, rawValue);
            }


            // Enum
            if (IsPulumiEnum(targetType))
            {
                return ConvertPulumiEnum(targetType, rawValue);
            }

            //
            // Primitives
            //
            if (targetType == typeof(string)) return rawValue.ToString()!;
            if (targetType == typeof(int)) return int.Parse(rawValue.ToString()!);
            if (targetType == typeof(bool)) return bool.Parse(rawValue.ToString()!);

            throw new NotSupportedException($"Cannot convert '{rawValue}' to {targetType}");
        }

        private static object ConvertOutputToInputList(Type innerTargetType, object output)
        {

            var listType = typeof(List<>).MakeGenericType(output.GetType());            
            var list = (IList)Activator.CreateInstance(listType)!;
            list.Add(output);

            var inputListType = typeof(InputList<>).MakeGenericType(innerTargetType);

            // szukamy: public static implicit operator InputList<T>(List<Output<T>> values)
            var op = inputListType.GetMethod(
                "op_Implicit",
                BindingFlags.Public | BindingFlags.Static,
                binder: null,
                types: [listType],
                modifiers: null)!;

            return op.Invoke(null, [list])!;
        }

        private static void CheckGenericArguments(Type targetType, Type sourceType, Type innerTargetType)
        {
            var innerSourceType = sourceType.GetGenericArguments()[0];
            if (innerTargetType != innerSourceType)
            {
                throw new InvalidOperationException($"Connot convert {sourceType.FullName} to {targetType.FullName}");
            }
        }

        private static bool IsGenericOutput(Type type)
        {
            return type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Output<>);
        }

        private static bool IsGenericInput(Type type)
        {
            return type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Input<>);
        }

        private static bool IsGenericInputList(Type type)
        {
            return type.IsGenericType && type.GetGenericTypeDefinition() == typeof(InputList<>);
        }

        private static bool IsPulumiEnum(Type type)
        {
            return type.GetCustomAttribute<EnumTypeAttribute>() != null;
        }

        private static object ConvertPulumiEnum(Type enumType, object rawValue)
        {
            var raw = rawValue.ToString()!;

            // znajdź wszystkie publiczne statyczne pola (np. SystemAssigned, UserAssigned)
            var props = enumType.GetProperties(BindingFlags.Public | BindingFlags.Static);

            foreach (var prop in props)
            {
                var propValue = prop.GetValue(null)!;

                // enumType ma prywatne pole "_value"
                var valueField = enumType.GetField("_value", BindingFlags.NonPublic | BindingFlags.Instance);
                if (valueField == null) continue;

                var enumString = valueField.GetValue(propValue)?.ToString();

                if (string.Equals(enumString, raw, StringComparison.OrdinalIgnoreCase))
                {
                    return propValue;
                }
            }

            throw new NotSupportedException(
                $"Cannot convert '{raw}' to Pulumi enum type {enumType.Name}");
        }

        private static object ConvertOutputToInput(Type innerType, object output)
        {
            var inputType = typeof(Input<>).MakeGenericType(innerType);
            // implicit operator Input<T>(Output<T> value)
            var op1 = inputType.GetMethod("op_Implicit", [output.GetType()])!;
            return op1.Invoke(null, [output])!;
        }

        private static object WrapInput(Type innerType, object value)
        {
            var inputType = typeof(Input<>).MakeGenericType(innerType);

            // implicit operator Input<T>(T value)
            var op1 = inputType.GetMethod("op_Implicit", [innerType]);
            if (op1 != null)
                return op1.Invoke(null, [value])!;

            // implicit operator Input<T>(Output<T> value)
            var outputType = typeof(Output<>).MakeGenericType(innerType);
            var op2 = inputType.GetMethod("op_Implicit", [outputType]);
            if (op2 != null)
            {
                var output = WrapOutput(innerType, value);
                return op2.Invoke(null, [output])!;
            }

            throw new InvalidOperationException($"Cannot wrap value into Input<{innerType}>");
        }

        private static object WrapOutput(Type innerType, object value)
        {
            var create = typeof(Output)
                .GetMethods()
                .First(m => m.Name == "Create" && m.IsGenericMethod)
                .MakeGenericMethod(innerType);

            return create.Invoke(null, [value])!;
        }

        private static object WrapInputList(Type elemType, object listObj)
        {
            var listType = typeof(List<>).MakeGenericType(elemType);

            // jeśli to jeszcze nie jest List<T> – spróbuj skonwertować
            if (!listType.IsInstanceOfType(listObj))
            {
                var tmp = (IList)Activator.CreateInstance(listType)!;
                foreach (var item in (IEnumerable)listObj)
                {
                    tmp.Add(ConvertValue(elemType, item!));
                }
                listObj = tmp;
            }

            var inputListType = typeof(InputList<>).MakeGenericType(elemType);

            // szukamy: public static implicit operator InputList<T>(List<T> values)
            var op = inputListType.GetMethod(
                "op_Implicit",
                BindingFlags.Public | BindingFlags.Static,
                binder: null,
                types: [listType],
                modifiers: null);

            if (op == null)
            {
                throw new InvalidOperationException($"No implicit operator InputList<{elemType.Name}>(List<{elemType.Name}>) found.");
            }

            return op.Invoke(null, [listObj])!;
        }

        private static object WrapInputMap(Type valueType, object dictObj)
        {
            var dictType = typeof(Dictionary<,>).MakeGenericType(typeof(string), valueType);

            // если это ещё не Dictionary<string,T> — пробуем привести
            if (!dictType.IsInstanceOfType(dictObj))
            {
                var tmp = (IDictionary)Activator.CreateInstance(dictType)!;
                foreach (DictionaryEntry kv in (IDictionary)dictObj)
                {
                    tmp[kv.Key] = ConvertValue(valueType, kv.Value!);
                }
                dictObj = tmp;
            }

            var inputMapType = typeof(InputMap<>).MakeGenericType(valueType);

            // ищем: public static implicit operator InputMap<TValue>(Dictionary<string, TValue> values)
            var op = inputMapType.GetMethod(
                "op_Implicit",
                BindingFlags.Public | BindingFlags.Static,
                binder: null,
                types: [dictType],
                modifiers: null);

            if (op == null)
            {
                throw new InvalidOperationException(
                    $"No implicit operator InputMap<{valueType.Name}>(Dictionary<string,{valueType.Name}>) found.");
            }

            return op.Invoke(null, [dictObj])!;
        }

        private static object ConvertToUnion(Type unionType, object rawValue)
        {
            var args = unionType.GetGenericArguments();
            var t0 = args[0];
            var t1 = args[1];

            // 1. Jeśli rawValue już jest Union<T0,T1>
            if (unionType.IsAssignableFrom(rawValue.GetType()))
                return rawValue;

            // 2. Spróbuj skonwertować rawValue do T0
            if (TryConvertToType(t0, rawValue, out var v0))
            {
                var fromT0 = unionType.GetMethod("FromT0", BindingFlags.Public | BindingFlags.Static)!;
                return fromT0.Invoke(null, [v0])!;
            }

            // 3. Spróbuj skonwertować rawValue do T1
            if (TryConvertToType(t1, rawValue, out var v1))
            {
                var fromT1 = unionType.GetMethod("FromT1", BindingFlags.Public | BindingFlags.Static)!;
                return fromT1.Invoke(null, [v1])!;
            }

            throw new NotSupportedException(
                $"Cannot convert '{rawValue}' to Union<{t0.Name},{t1.Name}>");
        }

        private static bool TryConvertToType(Type targetType, object rawValue, out object? result)
        {
            try
            {
                result = ConvertValue(targetType, rawValue);
                return true;
            }
            catch
            {
                result = null;
                return false;
            }
        }

        private static object ConvertToList(Type elemType, object raw)
        {
            var list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(elemType))!;

            if (raw is string s)
            {
                foreach (var part in s.Split(',', StringSplitOptions.RemoveEmptyEntries))
                    list.Add(ConvertValue(elemType, part.Trim()));
            }
            else if (raw is IEnumerable<object> enumerable)
            {
                foreach (var item in enumerable)
                    list.Add(ConvertValue(elemType, item));
            }
            else
            {
                list.Add(ConvertValue(elemType, raw));
            }

            return list;
        }
        private static object ConvertToDictionary(Type elemType, object raw)
        {
            var dict = (IDictionary)Activator.CreateInstance(
                typeof(Dictionary<,>).MakeGenericType(typeof(string), elemType))!;

            if (raw is IDictionary<string, object> rawDict)
            {
                foreach (var kv in rawDict)
                    dict[kv.Key] = ConvertValue(elemType, kv.Value);
            }

            return dict;
        }
    }
}
