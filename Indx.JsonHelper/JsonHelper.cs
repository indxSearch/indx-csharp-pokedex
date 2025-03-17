using System.Text.Json;

namespace Indx.JsonHelper;

public static class JsonHelper
{
    public static string GetFieldValue(string json, string fieldPath)
    {
        if (string.IsNullOrEmpty(json) || string.IsNullOrEmpty(fieldPath))
            return string.Empty;

        try
        {
            using var document = JsonDocument.Parse(json);
            var value = GetNestedFieldValue(document.RootElement, fieldPath.Split('.'));
            return FormatValue(value);
        }
        catch (JsonException)
        {
            // Handle any JSON parsing errors
            return string.Empty;
        }
    }

    private static object? GetNestedFieldValue(JsonElement element, string[] pathParts, int index = 0)
    {
        if (index >= pathParts.Length) return null;

        if (element.ValueKind == JsonValueKind.Array)
        {
            var results = new List<object>();
            foreach (var arrayElement in element.EnumerateArray())
            {
                var result = GetNestedFieldValue(arrayElement, pathParts, index);
                if (result != null) results.Add(result);
            }
            return results.Count > 0 ? results : null;
        }

        if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty(pathParts[index], out JsonElement nestedElement))
        {
            return index == pathParts.Length - 1 ? GetJsonElementValue(nestedElement) : GetNestedFieldValue(nestedElement, pathParts, index + 1);
        }

        return null;
    }

    private static object? GetJsonElementValue(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => element.TryGetInt64(out long l) ? l : (double?)element.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Array => element.EnumerateArray(),
            JsonValueKind.Object => element,
            _ => null
        };
    }

    private static string FormatValue(object? value)
    {
        if (value == null) return string.Empty;

        return value switch
        {
            List<object> list => string.Join(", ", list),
            JsonElement.ArrayEnumerator array => string.Join(", ", array),
            JsonElement.ObjectEnumerator objEnum => "{" + string.Join(", ", objEnum) + "}",
            _ => value.ToString() ?? string.Empty
        };
    }
} // end class JsonHelper
