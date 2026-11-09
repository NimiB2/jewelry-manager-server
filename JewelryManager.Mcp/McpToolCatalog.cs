using System.Reflection;
using ModelContextProtocol.Server;

namespace JewelryManager.Mcp;

/// <summary>Builds the business tools bound to one caller's API connection.</summary>
public static class McpToolCatalog
{
    private static readonly Type[] ToolTypes = [typeof(OrderTools), typeof(FinanceTools), typeof(CatalogTools)];

    public static IReadOnlyList<McpServerTool> Create(IBackendApi api)
    {
        var tools = new List<McpServerTool>();
        foreach (var type in ToolTypes)
        {
            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance))
            {
                if (method.GetCustomAttribute<McpServerToolAttribute>() is null) continue;
                tools.Add(McpServerTool.Create(method, _ => Activator.CreateInstance(type, api)!));
            }
        }

        return tools;
    }
}
