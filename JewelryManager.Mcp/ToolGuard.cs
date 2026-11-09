namespace JewelryManager.Mcp;

/// <summary>Turns failures inside a tool into a short envelope instead of an exception the model cannot read.</summary>
internal static class ToolGuard
{
    public static async Task<string> RunAsync(Func<Task<string>> action)
    {
        try
        {
            return await action();
        }
        catch (BackendApiException ex)
        {
            return ToolEnvelope.Error(ex.Message);
        }
        catch (ArgumentException ex)
        {
            return ToolEnvelope.Error(ex.Message);
        }
        catch (HttpRequestException)
        {
            return ToolEnvelope.Error("The system could not be reached right now");
        }
        catch (TaskCanceledException)
        {
            return ToolEnvelope.Error("The system took too long to answer");
        }
    }
}
