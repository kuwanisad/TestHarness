using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TestHarness.Core;

/// <summary>하네스가 밖으로 내보내는 JSON(도구 응답, events.jsonl)의 공통 형식.</summary>
public static class HarnessJson
{
    /// <summary>
    /// camelCase, 열거형은 이름(숫자 X - "InvalidCredentials"가 3보다 읽기 쉽다), null 필드 생략,
    /// 한글을 \uXXXX로 바꾸지 않음(AI와 사람이 그대로 읽게).
    /// </summary>
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string Serialize(object? value) => JsonSerializer.Serialize(value, Options);

    public static JsonElement ToElement(object? value) => JsonSerializer.SerializeToElement(value, Options);

    /// <summary>JSON 문자열을 요소로. 비었거나 깨졌으면 null(원문은 버리지 않고 호출 쪽이 따로 남긴다).</summary>
    public static JsonElement? TryParse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>응답이 너무 길어지지 않게 문자열을 자른다(잘렸으면 끝에 표시).</summary>
    public static string Truncate(string? text, int maxLength)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= maxLength) return text ?? "";
        return text[..maxLength] + $"…(+{text.Length - maxLength}자)";
    }
}
