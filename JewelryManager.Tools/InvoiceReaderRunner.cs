using System.Diagnostics;
using JewelryManager.Api.Features.Invoices;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace JewelryManager.Tools;

/// <summary>
/// Runs the real invoice reader over a folder of invoices and prints what it understood and how long it took,
/// so providers and models can be compared by hand. Nothing is saved; the key is never printed.
/// </summary>
public static class InvoiceReaderRunner
{
    private static readonly string[] Extensions = [".jpg", ".jpeg", ".png", ".webp", ".heic", ".pdf"];

    private static readonly Dictionary<string, string> ContentTypes = new()
    {
        [".jpg"] = "image/jpeg", [".jpeg"] = "image/jpeg", [".png"] = "image/png",
        [".webp"] = "image/webp", [".heic"] = "image/heic", [".pdf"] = "application/pdf",
    };

    public static async Task<int> RunAsync(string folder, string[] args, IConfiguration config)
    {
        if (!Directory.Exists(folder))
        {
            Console.WriteLine($"Folder not found: {folder}");
            return 1;
        }

        var options = config.GetSection(InvoiceReaderOptions.SectionName).Get<InvoiceReaderOptions>() ?? new InvoiceReaderOptions();
        options.Provider = Arg(args, "--provider") ?? options.Provider;
        options.Model = Arg(args, "--model") ?? options.Model;
        var keyEnv = Arg(args, "--key-env");
        if (keyEnv is not null) options.ApiKey = Environment.GetEnvironmentVariable(keyEnv) ?? "";

        if (!options.IsConfigured)
        {
            Console.WriteLine("Reader is not configured. Set InvoiceReader:Provider/Model/ApiKey in appsettings.Development.json,");
            Console.WriteLine("or pass --provider <gemini|openai|claude> --model <name> --key-env <ENV_VAR_NAME>.");
            return 1;
        }

        // The owner's real categories are not needed to judge accuracy; these stand in for them.
        var types = (Arg(args, "--types") ?? "חומרי גלם,אריזה,משלוחים,שיווק,כלי עבודה,אחר")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        var reader = new AiInvoiceReader(
            http, Options.Create(options),
            [new GeminiInvoiceProvider(), new OpenAiInvoiceProvider(), new ClaudeInvoiceProvider()],
            NullLogger<AiInvoiceReader>.Instance);

        if (!reader.IsAvailable)
        {
            Console.WriteLine($"Unknown provider '{options.Provider}'. Use gemini, openai or claude.");
            return 1;
        }

        var files = Directory.GetFiles(folder).Where(f => Extensions.Contains(Path.GetExtension(f).ToLowerInvariant())).Order().ToList();
        Console.WriteLine($"Provider: {options.Provider}   Model: {options.Model}   Files: {files.Count}");

        var times = new List<double>();
        var empty = 0;
        foreach (var file in files)
        {
            await using var stream = File.OpenRead(file);
            var watch = Stopwatch.StartNew();
            var result = await reader.ReadAsync(stream, Path.GetFileName(file), ContentTypes[Path.GetExtension(file).ToLowerInvariant()], types);
            watch.Stop();
            times.Add(watch.Elapsed.TotalSeconds);

            Console.WriteLine();
            Console.WriteLine($"{Path.GetFileName(file)}   ({watch.Elapsed.TotalSeconds:F1}s)");
            if (result is null)
            {
                empty++;
                Console.WriteLine("  no suggestion (unreadable, unsupported type, or the provider returned an error)");
                continue;
            }
            Console.WriteLine($"  amount:      {result.Amount?.ToString("0.00") ?? "-"}");
            Console.WriteLine($"  date:        {result.Date?.ToString("yyyy-MM-dd") ?? "-"}");
            Console.WriteLine($"  supplier:    {result.Supplier ?? "-"}");
            Console.WriteLine($"  type:        {result.TypeName ?? "-"}");
            Console.WriteLine($"  description: {result.Description ?? "-"}");
        }

        if (times.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine($"Average {times.Average():F1}s   Slowest {times.Max():F1}s   Without suggestion: {empty}/{files.Count}");
        }
        return 0;
    }

    private static string? Arg(string[] args, string name)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }
}
