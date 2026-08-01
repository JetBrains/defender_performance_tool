using System.Text;

namespace WindowsDefenderPerformanceTool;

/// <summary>
/// Minimal JSON escaping for the clipboard-export feature. Replaces Newtonsoft.Json
/// (~0.7 MB embedded) which was used for a single SerializeObject call.
/// </summary>
internal static class SimpleJson
{
    /// <summary>Returns the value as a quoted, escaped JSON string literal.</summary>
    public static string String(string value)
    {
        var sb = new StringBuilder(value.Length + 2);
        sb.Append('"');
        foreach (var c in value)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < ' ')
                        sb.Append("\\u").Append(((int)c).ToString("x4"));
                    else
                        sb.Append(c);
                    break;
            }
        }
        sb.Append('"');
        return sb.ToString();
    }
}
