using System.Text.Encodings.Web;
using System.Text.Json;

namespace JewelryManager.Mcp;

/// <summary>
/// How tool results are written as text for the model. Hebrew stays Hebrew: the default writer turns every
/// letter into a \uXXXX escape, which costs several times the tokens and is harder for the model to read.
/// </summary>
public static class ToolJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}
