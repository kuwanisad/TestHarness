using System.ComponentModel;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using TestHarness.Core;

namespace TestHarness.Daemon.Mcp;

/// <summary>도구 하나: 이름, 설명, 입력 스키마, 실행.</summary>
public sealed class McpToolDefinition
{
    internal McpToolDefinition(string name, string description, JsonObject inputSchema, object target, MethodInfo method)
    {
        Name = name;
        Description = description;
        InputSchema = inputSchema;
        Target = target;
        Method = method;
    }

    public string Name { get; }
    public string Description { get; }
    public JsonObject InputSchema { get; }
    internal object Target { get; }
    internal MethodInfo Method { get; }
}

/// <summary>
/// 도구 클래스의 [McpTool] 메서드를 찾아 MCP 도구 목록(이름/설명/JSON 스키마)을 만들고, tools/call의
/// 인자(JSON)를 메서드 파라미터로 바꿔 실행한다. 공식 SDK가 하는 일 중 하네스에 필요한 만큼만.
///
/// 파라미터 규칙: string, int/long/double, bool, enum(이름 문자열), string[], long[], JsonElement(자유 객체),
/// 그리고 위 타입의 nullable. 기본값이 있거나 nullable이면 선택 인자. CancellationToken은 자동으로 넣는다.
/// </summary>
public sealed class McpToolRegistry
{
    private readonly Dictionary<string, McpToolDefinition> _tools = new(StringComparer.Ordinal);

    public IReadOnlyCollection<McpToolDefinition> Tools => _tools.Values;

    /// <summary>target 객체의 [McpTool] 메서드를 전부 등록한다.</summary>
    public void Add(object target)
    {
        foreach (var method in target.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance))
        {
            var attribute = method.GetCustomAttribute<McpToolAttribute>();
            if (attribute == null) continue;

            if (_tools.ContainsKey(attribute.Name)) throw new InvalidOperationException($"도구 이름 중복: {attribute.Name}");

            var description = method.GetCustomAttribute<DescriptionAttribute>()?.Description ?? "";
            _tools[attribute.Name] = new McpToolDefinition(attribute.Name, description, BuildSchema(method), target, method);
        }
    }

    public bool TryGet(string name, out McpToolDefinition tool) => _tools.TryGetValue(name, out tool!);

    /// <summary>도구를 실행해 반환값을 돌려준다. 인자가 틀리면 HarnessException(어떤 인자가 왜 틀렸는지).</summary>
    public async Task<object?> InvokeAsync(McpToolDefinition tool, JsonElement? arguments, CancellationToken ct)
    {
        var parameters = tool.Method.GetParameters();
        var values = new object?[parameters.Length];

        var provided = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        if (arguments is { ValueKind: JsonValueKind.Object } args)
        {
            foreach (var property in args.EnumerateObject()) provided[property.Name] = property.Value;
        }

        var known = parameters.Select(p => p.Name!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var unknown = provided.Keys.Where(k => !known.Contains(k)).ToList();
        if (unknown.Count > 0)
        {
            throw new HarnessException($"{tool.Name}: 모르는 인자 {string.Join(", ", unknown)}",
                $"받는 인자: {string.Join(", ", parameters.Where(p => p.ParameterType != typeof(CancellationToken)).Select(p => p.Name))}");
        }

        for (var i = 0; i < parameters.Length; i++)
        {
            var parameter = parameters[i];
            if (parameter.ParameterType == typeof(CancellationToken))
            {
                values[i] = ct;
                continue;
            }

            if (provided.TryGetValue(parameter.Name!, out var element) && element.ValueKind != JsonValueKind.Null)
            {
                values[i] = Convert(tool.Name, parameter, element);
            }
            else if (parameter.HasDefaultValue)
            {
                values[i] = parameter.DefaultValue;
            }
            else if (IsNullable(parameter))
            {
                values[i] = null;
            }
            else
            {
                throw new HarnessException($"{tool.Name}: 필수 인자 '{parameter.Name}'가 없음");
            }
        }

        object? result;
        try
        {
            result = tool.Method.Invoke(tool.Target, values);
        }
        catch (TargetInvocationException ex) when (ex.InnerException != null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            throw;
        }

        if (result is Task task)
        {
            await task;
            // 실제 객체 타입이 아니라 선언된 반환 타입으로 판단한다 - async Task 메서드도 실행 시에는
            // 제네릭 상태 머신 객체(Result = VoidTaskResult)라서.
            var declared = tool.Method.ReturnType;
            return declared.IsGenericType && declared.GetGenericTypeDefinition() == typeof(Task<>)
                ? declared.GetProperty("Result")!.GetValue(task)
                : null;
        }

        return result;
    }

    private static object? Convert(string toolName, ParameterInfo parameter, JsonElement element)
    {
        var type = Nullable.GetUnderlyingType(parameter.ParameterType) ?? parameter.ParameterType;
        try
        {
            if (type == typeof(JsonElement)) return element.Clone();

            if (type == typeof(string))
            {
                // 숫자/불을 문자열 자리에 줘도 받아준다(AI가 "5" 대신 5를 보내는 경우).
                return element.ValueKind == JsonValueKind.String ? element.GetString() : element.GetRawText();
            }

            if (type == typeof(string[]))
            {
                // 배열 대신 문자열 하나("LoginServer" 또는 "A,B")를 줘도 받아준다.
                if (element.ValueKind == JsonValueKind.String)
                {
                    return element.GetString()!.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                }

                return element.EnumerateArray().Select(e => e.ValueKind == JsonValueKind.String ? e.GetString()! : e.GetRawText()).ToArray();
            }

            if (type == typeof(long[]))
            {
                // 배열 대신 숫자 하나나 "12,15" 문자열을 줘도 받아준다.
                if (element.ValueKind == JsonValueKind.Number) return new[] { element.GetInt64() };
                if (element.ValueKind == JsonValueKind.String)
                {
                    return element.GetString()!.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .Select(t => long.Parse(t, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
                }

                return element.EnumerateArray()
                    .Select(e => e.ValueKind == JsonValueKind.String ? long.Parse(e.GetString()!, System.Globalization.CultureInfo.InvariantCulture) : e.GetInt64())
                    .ToArray();
            }

            if (type.IsEnum)
            {
                var text = element.ValueKind == JsonValueKind.String ? element.GetString() : element.GetRawText();
                if (Enum.TryParse(type, text, ignoreCase: true, out var parsed) && Enum.IsDefined(type, parsed!)) return parsed;
                throw new HarnessException($"{toolName}: '{parameter.Name}' 값 '{text}'을(를) 모름",
                    $"가능: {string.Join(", ", Enum.GetNames(type))}");
            }

            if (type == typeof(bool) && element.ValueKind == JsonValueKind.String)
            {
                return bool.Parse(element.GetString()!);
            }

            if ((type == typeof(int) || type == typeof(long) || type == typeof(double)) && element.ValueKind == JsonValueKind.String)
            {
                return System.Convert.ChangeType(element.GetString(), type, System.Globalization.CultureInfo.InvariantCulture);
            }

            return element.Deserialize(type, HarnessJson.Options);
        }
        catch (Exception ex) when (ex is JsonException or FormatException or InvalidOperationException or OverflowException)
        {
            throw new HarnessException($"{toolName}: '{parameter.Name}' 값을 {Describe(type)}(으)로 읽지 못함 ({element.GetRawText()})");
        }
    }

    private static bool IsNullable(ParameterInfo parameter)
    {
        if (Nullable.GetUnderlyingType(parameter.ParameterType) != null) return true;
        if (parameter.ParameterType.IsValueType) return false;
        return new NullabilityInfoContext().Create(parameter).WriteState == NullabilityState.Nullable;
    }

    private static JsonObject BuildSchema(MethodInfo method)
    {
        var properties = new JsonObject();
        var required = new JsonArray();

        foreach (var parameter in method.GetParameters())
        {
            if (parameter.ParameterType == typeof(CancellationToken)) continue;

            var schema = TypeSchema(parameter.ParameterType);
            var description = parameter.GetCustomAttribute<DescriptionAttribute>()?.Description;
            if (parameter.HasDefaultValue && parameter.DefaultValue != null)
            {
                var defaultText = parameter.DefaultValue is bool b ? (b ? "true" : "false") : parameter.DefaultValue.ToString();
                description = string.IsNullOrEmpty(description) ? $"기본값 {defaultText}" : $"{description} (기본값 {defaultText})";
            }

            if (!string.IsNullOrEmpty(description)) schema["description"] = description;
            properties[parameter.Name!] = schema;

            if (!parameter.HasDefaultValue && !IsNullable(parameter)) required.Add(parameter.Name!);
        }

        var root = new JsonObject { ["type"] = "object", ["properties"] = properties };
        if (required.Count > 0) root["required"] = required;
        return root;
    }

    private static JsonObject TypeSchema(Type parameterType)
    {
        var type = Nullable.GetUnderlyingType(parameterType) ?? parameterType;
        if (type == typeof(string)) return new JsonObject { ["type"] = "string" };
        if (type == typeof(bool)) return new JsonObject { ["type"] = "boolean" };
        if (type == typeof(int) || type == typeof(long)) return new JsonObject { ["type"] = "integer" };
        if (type == typeof(double)) return new JsonObject { ["type"] = "number" };
        if (type == typeof(string[])) return new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } };
        if (type == typeof(long[])) return new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "integer" } };
        if (type == typeof(JsonElement)) return new JsonObject { ["type"] = "object" };
        if (type.IsEnum)
        {
            var names = new JsonArray();
            foreach (var name in Enum.GetNames(type)) names.Add(name);
            return new JsonObject { ["type"] = "string", ["enum"] = names };
        }

        throw new InvalidOperationException($"도구 파라미터로 지원하지 않는 타입: {type.Name}");
    }

    private static string Describe(Type type) =>
        type == typeof(int) || type == typeof(long) ? "정수"
        : type == typeof(double) ? "숫자"
        : type == typeof(bool) ? "true/false"
        : type.Name;
}
