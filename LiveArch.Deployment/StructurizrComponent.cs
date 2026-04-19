using LiveArch.Deployment.Controls;
using LiveArch.Deployment.Transformers;
using Pulumi;
using Pulumi.AzureNative.App;
using Pulumi.AzureNative.Resources;
using Pulumi.AzureNative.Web;
using Pulumi.DockerBuild;
using Structurizr;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Type = System.Type;

namespace LiveArch.Deployment
{
    public partial class StructurizrComponent
    {
        [GeneratedRegex(@"\$\{([a-zA-Z0-9_\.\:\-]+)\}", RegexOptions.Multiline, 1000)]
        private static partial Regex InterpolationRegex();
        private static readonly Regex VarRegex = InterpolationRegex();
        private readonly string parent = Guid.NewGuid().ToString();
        private readonly string owner = Guid.NewGuid().ToString();
        private int level = 0;
        private readonly string environment;
        private readonly IReadOnlyDictionary<string, object> rootVars;
        private Workspace workspace;
        private readonly Dictionary<string, Type> resourceTypes = new();
        private Dictionary<string, MethodInfo> invokeMethods = new();
        private Dictionary<(Element, IReadOnlyDictionary<string, object>), object> newResources = new();
        private Dictionary<(Element, IReadOnlyDictionary<string, object>), object> oldResources = new();
        private Dictionary<object, object> childInputWrappers = new();

        private Dictionary<Type, Dictionary<string, PropertyInfo>> allInputProps = new();
        private readonly Dictionary<Type, Dictionary<string, MemberInfo>> _outputMembersCache = new();

        private readonly PropertyInfo inputAttrNameProp = typeof(InputAttribute).GetProperty("Name", BindingFlags.Instance | BindingFlags.NonPublic)!;
        private readonly InvokeOptions? invokeOptions = null;
        private readonly CustomResourceOptions? customResourceOptions = null;

        public IReadOnlyDictionary<(Element, IReadOnlyDictionary<string, object>), object> NewResources => newResources;
        public IReadOnlyDictionary<(Element, IReadOnlyDictionary<string, object>), object> OldResources => oldResources;

        public StructurizrComponent(string workspacePath, string environment, IReadOnlyDictionary<string, object> variables)
        {
            var json = new FileInfo(workspacePath);
            workspace = WorkspaceUtils.LoadWorkspaceFromJson(json);
            this.environment = environment;
            rootVars = new Dictionary<string, object>(variables)
            {
                [owner] = workspace,
                ["level"] = ++level
            };
            CachePulumiTypes(typeof(Image), typeof(ResourceGroup), typeof(ForEachLoop));
        }

        public async Task ProcessWorkspaceAsync(CancellationToken cancellationToken)
        {
            foreach (var deployNode in workspace.Model.DeploymentNodes.On(environment, SubstituteVariables(rootVars)))
            {
                await ProcessDeploymentNodeAsync(deployNode, rootVars, cancellationToken);
            }
        }

        private void CachePulumiTypes(params Type[] entryTypes)
        {
            entryTypes.Select(x => x.Assembly).Distinct().ToList().ForEach(CacheAssamblyTypes);
        }

        private void CacheAssamblyTypes(Assembly assembly)
        {
            var types = assembly.GetTypes();
            foreach (var resType in types)
            {
                var attr = resType.GetCustomAttribute<ResourceTypeAttribute>(true);
                if (attr != null)
                {
                    resourceTypes.Add(attr.Type, resType);
                }
            }

            foreach (var type in types)
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

        private object SubstituteVariables(string input, IReadOnlyDictionary<string, object> vars)
        {
            var direct = vars.FirstOrDefault(kv => input == $"${{{kv.Key}}}");
            if (direct.Key != null)
            {
                return direct.Value;
            }
            return VarRegex.Replace(input, match =>
            {
                var name = match.Groups[1].Value;

                if (!vars.TryGetValue(name, out var value))
                {
                    throw new InvalidOperationException($"Variable '${{{name}}}' is not defined.");
                }

                return (string)ConvertValue(typeof(string), value, vars);
            });
        }

        private Func<string, object> SubstituteVariables(IReadOnlyDictionary<string, object> vars)
        {
            return s => SubstituteVariables(s, vars);
        }


        protected async Task ProcessDeploymentNodeAsync(DeploymentNode deployNode, IReadOnlyDictionary<string, object> vars, CancellationToken cancellationToken)
        {
            var deploymentNode = new DeploymentNodeAdapter(deployNode, SubstituteVariables(vars));
            if (deploymentNode.IsDisabled == false)
            {
                var res = await CreateNodeAsync(deploymentNode, vars, cancellationToken);

                var infraNodes = deployNode.InfrastructureNodes.On(environment, SubstituteVariables(vars))
                    .Select(x => new InfrastructureNodeAdapter(x, SubstituteVariables(vars)))
                    .Where(x => x.IsDisabled == false)
                    .ToList();

                if (res is ForEachLoop loop)
                {
                    var sourceElement = infraNodes.FirstOrDefault(x => x.Technology == ForEachSource.Technology);
                    if (sourceElement != null)
                    {
                        infraNodes.Remove(sourceElement);
                        var sourceVars = new Dictionary<string, object>(vars)
                        {
                            [parent] = vars,
                            [owner] = loop,
                            ["level"] = ++level
                        };
                        var sourceComponent = await CreateNodeAsync(sourceElement, sourceVars, cancellationToken) as ForEachSource;
                        if (sourceComponent != null)
                        {
                            sourceComponent.Source.Apply(async items =>
                            {
                                foreach (var item in items)
                                {
                                    var loopVars = new Dictionary<string, object>(vars)
                                    {
                                        [loop.Name] = item,
                                        [parent] = vars,
                                        [owner] = loop!,
                                        ["level"] = ++level
                                    };
                                    await CreateChildResources(deployNode, infraNodes, loopVars, cancellationToken);
                                }
                            });
                        }
                    }
                }
                else
                {
                    var childVars = new Dictionary<string, object>(vars)
                    {
                        [parent] = vars,
                        [owner] = res!,
                        ["level"] = ++level
                    };
                    await CreateChildResources(deployNode, infraNodes, childVars, cancellationToken);
                }
            }
        }

        private async Task CreateChildResources(DeploymentNode deployNode, List<InfrastructureNodeAdapter> infraNodes, IReadOnlyDictionary<string, object> childVars, CancellationToken cancellationToken)
        {
            foreach (var infraNode in infraNodes)
            {
                await CreateNodeAsync(infraNode, childVars, cancellationToken);
            }

            foreach (var containerInstance in deployNode.ContainerInstances.On(environment, SubstituteVariables(childVars)))
            {
                await ProcessContainerInstanceAsync(containerInstance!, childVars, cancellationToken);
            }

            foreach (var childNode in deployNode.Children)
            {
                await ProcessDeploymentNodeAsync(childNode!, childVars, cancellationToken);
            }
        }

        private async Task ProcessContainerInstanceAsync(ContainerInstance containerInstance, IReadOnlyDictionary<string, object> vars, CancellationToken cancellationToken)
        {
            var container = new ContainerInstanceAdapter(containerInstance, SubstituteVariables(vars));
            if (container.IsDisabled == false)
            {
                await BuildContainerInstance(containerInstance, vars, cancellationToken);
                await CreateNodeAsync(container, vars, cancellationToken);
            }
        }

        private async Task BuildContainerInstance(ContainerInstance containerInstance, IReadOnlyDictionary<string, object> vars, CancellationToken cancellationToken)
        {
            await CreateNodeAsync(new ContainerBuildAdapter(containerInstance.Container, SubstituteVariables(vars)), vars, cancellationToken);
        }

        private async Task<object?> CreateNodeAsync(IDeploymentNode deployNode, IReadOnlyDictionary<string, object> vars, CancellationToken cancellationToken)
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
                            PropagateParentProperties(deployNode.Parent, param, paramInputProps, vars);
                        }

                        ApplyRelations(deployNode, param, vars);

                        foreach ((var propName, var propVal) in deployNode.Properties)
                        {
                            SetProperty(param, propName, propVal, paramInputProps, vars);
                        }

                        var task = (Task)invoke.Invoke(null, [param, invokeOptions!])!;
                        await task.ConfigureAwait(false);

                        var resultProperty = task.GetType().GetProperty("Result");
                        var resource = resultProperty!.GetValue(task);

                        oldResources.Add((deployNode.Node, vars), resource!);
                        oldResources.TryAdd((deployNode.Node, GetLoopScopedVars(vars)), resource!);
                        return resource;
                    }
                }
                else
                {
                    var paramType = type.GetConstructors()[0].GetParameters()[1];
                    var param = Activator.CreateInstance(paramType.ParameterType)!;
                    var paramInputProps = GetInputProps(paramType.ParameterType);

                    if (deployNode.Parent != null)
                    {
                        PropagateParentProperties(deployNode.Parent, param, paramInputProps, vars);
                    }

                    ApplyRelations(deployNode, param, vars);

                    foreach ((var propName, var propVal) in deployNode.Properties)
                    {
                        SetProperty(param, propName, propVal, paramInputProps, vars);
                    }

                    if (!deployNode.Properties.TryGetValue("var", out var resVar) &&
                        (!deployNode.Properties.TryGetValue("structurizr.dsl.identifier", out resVar) || Guid.TryParse(resVar, out _)) &&
                        !deployNode.Properties.TryGetValue("name", out resVar))
                    {
                        resVar = deployNode.Node.Name;
                    }

                    var newRes = Activator.CreateInstance(type, [SubstituteVariables(resVar, vars), param, customResourceOptions!]);
                    newResources.Add((deployNode.Node, vars), newRes!);
                    newResources.TryAdd((deployNode.Node, GetLoopScopedVars(vars)), newRes!);
                    return newRes;
                }
            }
            return null;
        }

        private IReadOnlyCollection<ITransformer> GetTransformers(Dictionary<string, string> properties)
        {
            var transformers = new List<ITransformer>();
            foreach ((var name, var get) in TransformerRegistry.Registry)
            {
                if (properties.TryGetValue(name, out var param))
                {
                    var transformer = get(param);
                    transformers.Add(transformer);
                }
            }
            return transformers;
        }

        private void ApplyRelations(IDeploymentNode deployNode, object param, IReadOnlyDictionary<string, object> vars)
        {
            foreach (var relation in deployNode.Relationships)
            {
                if (TryGetResourceByNode(relation.Destination, vars, out var source))
                {
                    if (relation.Properties.TryGetValue("source", out var sourcePath) && relation.Properties.TryGetValue("target", out var targetPath))
                    {
                        ApplyDependency(source!, param, sourcePath, targetPath, vars, GetTransformers(relation.Properties));
                    }
                }
            }

            if (deployNode.Node is ContainerInstance ci && newResources.TryGetValue((ci.Container, vars), out var image) && image is Image dockerImage)
            {
                if (param is WebAppArgs web)
                {
                    SetProperty(web, "siteConfig.linuxFxVersion", Output.Format($"DOCKER|{dockerImage.Ref}"), GetInputProps(typeof(WebAppArgs)), vars);
                }
                else if (param is ContainerAppArgs app)
                {
                    SetProperty(app, "template.containers.image", dockerImage.Ref, GetInputProps(typeof(ContainerAppArgs)), vars);
                }
                else if (param is DeploymentArgs k8s)
                {
                    SetProperty(k8s, "spec.template.spec.containers.image", dockerImage.Ref, GetInputProps(typeof(DeploymentArgs)), vars);
                }
            }
        }

        private void PropagateParentProperties(IDeploymentNode deployNode, object param, Dictionary<string, PropertyInfo> paramInputProps, IReadOnlyDictionary<string, object> vars)
        {
            var parentVars = vars;
            if (deployNode.Parent != null && TryGetParentVars(vars, out parentVars))
            {
                PropagateParentProperties(deployNode.Parent, param, paramInputProps, parentVars!);
            }
            if (!TryGetResourceByNode(deployNode.Node, parentVars ?? vars, out var resource))
            {
                return;
            }

            if (ResourceHierarchy.Registry.TryGetValue(resource!.GetType(), out var rules))
            {
                foreach (var rule in rules)
                {
                    var value = rule.ParentOutputProperty(resource);
                    if (value != null)
                    {
                        foreach (var targetProp in rule.TargetInputProperties)
                        {
                            SetProperty(param, targetProp, value, paramInputProps, vars);
                        }
                    }
                }
            }
        }

        private bool TryGetResourceByNode(Element node, IReadOnlyDictionary<string, object> vars, out object? resource)
        {
            if (!oldResources.TryGetValue((node, vars), out resource) &&
                !newResources.TryGetValue((node, vars), out resource) &&
                !oldResources.TryGetValue((node, GetLoopScopedVars(vars)), out resource) &&
                !newResources.TryGetValue((node, GetLoopScopedVars(vars)), out resource) &&
                !oldResources.TryGetValue((node, rootVars), out resource) &&
                !newResources.TryGetValue((node, rootVars), out resource))
            {
                if (node is StaticStructureElement)
                {
                    return false;
                }
                throw new InvalidOperationException($"Resource for node {node.Name} is out of scope");
            }

            return true;
        }

        private bool TryGetParentVars(IReadOnlyDictionary<string, object> vars, out IReadOnlyDictionary<string, object>? parentVars)
        {
            if (vars.TryGetValue(parent, out var parentVarObj))
            {
                parentVars = (IReadOnlyDictionary<string, object>)parentVarObj;
                return true;
            }
            parentVars = null;
            return false;
        }

        private IReadOnlyDictionary<string, object> GetLoopScopedVars(IReadOnlyDictionary<string, object> vars)
        {
            if (vars.TryGetValue(owner, out var ownerResource)
                && ownerResource is not ForEachLoop
                && TryGetParentVars(vars, out var parentVars))
            {
                return GetLoopScopedVars(parentVars!);
            }
            return vars;
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

        public void ApplyDependency(object source, object target, string sourcePath, string targetPath, IReadOnlyDictionary<string, object> vars, IReadOnlyCollection<ITransformer> transformers)
        {
            var value = GetOutputValue(source, sourcePath);
            if (value == null)
                return;

            var inputProps = GetInputProps(target.GetType());
            if (transformers.Count > 0)
            {
                foreach (var transformer in transformers)
                {
                    value = ConvertValue(transformer.InputType, value, vars);
                    value = transformer.Transform(value);
                }
            }
            SetProperty(target, targetPath, value, inputProps, vars);
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

        public void SetProperty(object target, string path, object value, Dictionary<string, PropertyInfo> inputProps, IReadOnlyDictionary<string, object> vars)
        {
            var parts = path.Split('.', 2);

            if (parts.Length == 1)
            {
                // leaf property
                if (inputProps.TryGetValue(parts[0], out var prop))
                {
                    object converted = ConvertValue(prop.PropertyType, value, vars);
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
                current = CreateNestedInstance(headProp.PropertyType, vars, out var unwrapped);
                childInputWrappers[current] = unwrapped ?? current;
                headProp.SetValue(target, current);
            }

            var nestedProps = GetInputProps(GetUnderlyingArgsType(headProp.PropertyType));

            SetProperty(childInputWrappers[current], tail, value, nestedProps, vars);
        }

        private object CreateNestedInstance(Type type, IReadOnlyDictionary<string, object> vars, out object? unwrapped)
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
                return WrapInputList(elem, list, vars);
            }

            // InputMap<T>
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(InputMap<>))
            {
                var elem = type.GetGenericArguments()[0];
                var dict = Activator.CreateInstance(typeof(Dictionary<,>)
                    .MakeGenericType(typeof(string), elem))!;
                unwrapped = dict;
                return WrapInputMap(elem, dict, vars);
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

        public object ConvertValue(Type targetType, object sourceValue, IReadOnlyDictionary<string, object> vars)
        {
            if (sourceValue is string str)
            {
                sourceValue = SubstituteVariables(str, vars);
            }

            if (sourceValue == null)
            {
                return null!;
            }

            var sourceType = sourceValue.GetType();

            // source value is Output
            if (IsGenericOutput(sourceType))
            {
                var innerTargetType = targetType.GetGenericArguments()[0]!;

                // target is Input
                if (IsGenericInput(targetType))
                {
                    CheckGenericArguments(targetType, sourceType, innerTargetType);
                    return ConvertOutputToInput(innerTargetType, sourceValue);
                }

                if (IsGenericInputList(targetType))
                {
                    CheckGenericArguments(targetType, sourceType, innerTargetType);
                    return ConvertOutputToInputList(innerTargetType, sourceValue);
                }

                throw new NotSupportedException($"Cannot convert {sourceType.FullName} to {targetType.FullName}");
            }

            // Если значение уже подходит — возвращаем как есть
            if (targetType.IsAssignableFrom(sourceType))
            {
                return sourceValue;
            }

            // Input<T>
            if (IsGenericInput(targetType))
            {
                var innerType = targetType.GetGenericArguments()[0];

                // если rawValue уже совместим с innerType → используем implicit operator
                if (innerType.IsAssignableFrom(sourceType))
                    return WrapInput(innerType, sourceValue);

                // иначе конвертируем и оборачиваем
                var converted = ConvertValue(innerType, sourceValue, vars);
                return WrapInput(innerType, converted);
            }

            // InputList<T>
            if (targetType.IsGenericType && targetType.GetGenericTypeDefinition() == typeof(InputList<>))
            {
                var elemType = targetType.GetGenericArguments()[0];
                var list = ConvertToList(elemType, sourceValue, vars);
                return WrapInputList(elemType, list, vars);
            }

            // InputMap<T>
            if (targetType.IsGenericType && targetType.GetGenericTypeDefinition() == typeof(InputMap<>))
            {
                var elemType = targetType.GetGenericArguments()[0];
                var dict = ConvertToDictionary(elemType, sourceValue, vars);
                return WrapInputMap(elemType, dict, vars);
            }

            // Union<T0,T1>
            if (targetType.IsGenericType &&
                targetType.GetGenericTypeDefinition() == typeof(Union<,>))
            {
                return ConvertToUnion(targetType, sourceValue, vars);
            }


            // Enum
            if (IsPulumiEnum(targetType))
            {
                return ConvertPulumiEnum(targetType, sourceValue, vars);
            }

            //
            // Primitives
            //
            if (targetType == typeof(string)) return sourceValue.ToString()!;
            if (targetType == typeof(int)) return int.Parse(sourceValue.ToString()!);
            if (targetType == typeof(bool)) return bool.Parse(sourceValue.ToString()!);

            throw new NotSupportedException($"Cannot convert '{sourceValue}' to {targetType}");
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

        private object ConvertPulumiEnum(Type enumType, object sourceValue, IReadOnlyDictionary<string, object> vars)
        {
            var str = (string)ConvertValue(typeof(string), sourceValue, vars);

            // znajdź wszystkie publiczne statyczne pola (np. SystemAssigned, UserAssigned)
            var props = enumType.GetProperties(BindingFlags.Public | BindingFlags.Static);

            foreach (var prop in props)
            {
                var propValue = prop.GetValue(null)!;

                // enumType ma prywatne pole "_value"
                var valueField = enumType.GetField("_value", BindingFlags.NonPublic | BindingFlags.Instance);
                if (valueField == null) continue;

                var enumString = valueField.GetValue(propValue)?.ToString();

                if (string.Equals(enumString, str, StringComparison.OrdinalIgnoreCase))
                {
                    return propValue;
                }
            }

            throw new NotSupportedException(
                $"Cannot convert '{sourceValue}' to Pulumi enum type {enumType.Name}");
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

        private object WrapInputList(Type elemType, object listObj, IReadOnlyDictionary<string, object> vars)
        {
            var listType = typeof(List<>).MakeGenericType(elemType);

            // jeśli to jeszcze nie jest List<T> – spróbuj skonwertować
            if (!listType.IsInstanceOfType(listObj))
            {
                var tmp = (IList)Activator.CreateInstance(listType)!;
                foreach (var item in (IEnumerable)listObj)
                {
                    tmp.Add(ConvertValue(elemType, item!, vars));
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

        private object WrapInputMap(Type valueType, object dictObj, IReadOnlyDictionary<string, object> vars)
        {
            var dictType = typeof(Dictionary<,>).MakeGenericType(typeof(string), valueType);

            // если это ещё не Dictionary<string,T> — пробуем привести
            if (!dictType.IsInstanceOfType(dictObj))
            {
                var tmp = (IDictionary)Activator.CreateInstance(dictType)!;
                foreach (DictionaryEntry kv in (IDictionary)dictObj)
                {
                    tmp[kv.Key] = ConvertValue(valueType, kv.Value!, vars);
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

        private object ConvertToUnion(Type unionType, object rawValue, IReadOnlyDictionary<string, object> vars)
        {
            var args = unionType.GetGenericArguments();
            var t0 = args[0];
            var t1 = args[1];

            // 1. Jeśli rawValue już jest Union<T0,T1>
            if (unionType.IsAssignableFrom(rawValue.GetType()))
                return rawValue;

            // 2. Spróbuj skonwertować rawValue do T0
            if (TryConvertToType(t0, rawValue, vars, out var v0))
            {
                var fromT0 = unionType.GetMethod("FromT0", BindingFlags.Public | BindingFlags.Static)!;
                return fromT0.Invoke(null, [v0])!;
            }

            // 3. Spróbuj skonwertować rawValue do T1
            if (TryConvertToType(t1, rawValue, vars, out var v1))
            {
                var fromT1 = unionType.GetMethod("FromT1", BindingFlags.Public | BindingFlags.Static)!;
                return fromT1.Invoke(null, [v1])!;
            }

            throw new NotSupportedException(
                $"Cannot convert '{rawValue}' to Union<{t0.Name},{t1.Name}>");
        }

        private bool TryConvertToType(Type targetType, object rawValue, IReadOnlyDictionary<string, object> vars, out object? result)
        {
            try
            {
                result = ConvertValue(targetType, rawValue, vars);
                return true;
            }
            catch
            {
                result = null;
                return false;
            }
        }

        private object ConvertToList(Type elemType, object raw, IReadOnlyDictionary<string, object> vars)
        {
            var list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(elemType))!;

            if (raw is string s)
            {
                foreach (var part in s.Split(',', StringSplitOptions.RemoveEmptyEntries))
                {
                    list.Add(ConvertValue(elemType, part.Trim(), vars));
                }
            }
            else if (raw is IEnumerable<object> enumerable)
            {
                foreach (var item in enumerable)
                {
                    list.Add(ConvertValue(elemType, item, vars));
                }
            }
            else
            {
                list.Add(ConvertValue(elemType, raw, vars));
            }

            return list;
        }

        private object ConvertToDictionary(Type elemType, object sourceValue, IReadOnlyDictionary<string, object> vars)
        {
            var dict = (IDictionary)Activator.CreateInstance(
                typeof(Dictionary<,>).MakeGenericType(typeof(string), elemType))!;

            if (sourceValue is IDictionary<string, object> rawDict)
            {
                foreach (var kv in rawDict)
                {
                    dict[kv.Key] = ConvertValue(elemType, kv.Value, vars);
                }
            }
            else
            {
                throw new NotSupportedException($"Cannot convert '{sourceValue}' to Dictionary<string,{elemType.Name}>");
            }

            return dict;
        }
    }
}
