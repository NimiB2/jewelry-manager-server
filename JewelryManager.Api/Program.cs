using FirebaseAdmin;
using Google.Apis.Auth.OAuth2;
using JewelryManager.Api.Auth;
using JewelryManager.Api.Common.Configuration;
using JewelryManager.Api.Common.Filters;
using JewelryManager.Api.Data;
using JewelryManager.Api.Features.Collections;
using JewelryManager.Api.Features.Expenses;
using JewelryManager.Api.Features.Finances;
using JewelryManager.Api.Features.Incomes;
using JewelryManager.Api.Features.Invoices;
using JewelryManager.Api.Features.Orders;
using JewelryManager.Api.Features.Pricing;
using JewelryManager.Api.Features.Products;
using JewelryManager.Api.Features.Settings;
using JewelryManager.Api.Features.Shopify;
using JewelryManager.Api.Features.Tasks;
using JewelryManager.Api.Features.Users;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using System.Text.Json.Serialization;

var builder = WebApplication.CreateBuilder(args);

// ── Controllers ──────────────────────────────────────────────────────────────
// AddControllers scans for [ApiController] classes and registers their routes.
// The GlobalExceptionFilter is added here so it runs for every controller action.
// Enums travel as UPPER_SNAKE strings ("OWNER", "SUPER_ADMIN") — the contract the client expects.
builder.Services.AddControllers(options =>
        options.Filters.Add<GlobalExceptionFilter>())
    .AddJsonOptions(options =>
        options.JsonSerializerOptions.Converters.Add(
            new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseUpper)));

// ── OpenAPI (Swagger) ─────────────────────────────────────────────────────────
builder.Services.AddOpenApi();

// ── Database ──────────────────────────────────────────────────────────────────
// Registers AppDbContext as a scoped service (one instance per HTTP request).
// Npgsql is the PostgreSQL provider for EF Core.
var connectionString = PostgresConnectionString.Normalize(
    builder.Configuration.GetConnectionString("Default")
    ?? throw new InvalidOperationException("ConnectionStrings:Default is not configured."));

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(connectionString));

// ── Firebase ──────────────────────────────────────────────────────────────────
// Initialize the Firebase Admin SDK once (singleton for the app lifetime).
// In the cloud the service account arrives as the JSON text itself (Firebase:CredentialJson, an env var,
// since there is no file to ship); locally it is a file whose path is Firebase:CredentialPath.
var firebaseCredentialJson = builder.Configuration["Firebase:CredentialJson"];
var firebaseCredentialPath = builder.Configuration["Firebase:CredentialPath"];
if (string.IsNullOrWhiteSpace(firebaseCredentialJson) && string.IsNullOrWhiteSpace(firebaseCredentialPath))
    throw new InvalidOperationException("Neither Firebase:CredentialJson nor Firebase:CredentialPath is configured.");

// GoogleCredential.FromFile/FromJson are flagged as deprecated but remain the correct
// approach for loading service account JSON in FirebaseAdmin 3.x.
// The suggested CredentialFactory alternative is not yet available in this SDK version.
#pragma warning disable CS0618
FirebaseApp.Create(new AppOptions
{
    Credential = !string.IsNullOrWhiteSpace(firebaseCredentialJson)
        ? GoogleCredential.FromJson(firebaseCredentialJson)
        : GoogleCredential.FromFile(firebaseCredentialPath),
});
#pragma warning restore CS0618

// Register FirebaseAuth as a singleton so middleware can inject it.
builder.Services.AddSingleton(FirebaseAdmin.Auth.FirebaseAuth.DefaultInstance);

// ── HttpContextAccessor ───────────────────────────────────────────────────────
// Required so that scoped services (e.g. CurrentUserAccessor) can read
// HttpContext.Items outside of controllers.
builder.Services.AddHttpContextAccessor();

// ── Auth ──────────────────────────────────────────────────────────────────────
// CurrentUserAccessor is scoped — one per request, same lifetime as AppDbContext.
builder.Services.AddScoped<CurrentUserAccessor>();

// ── Features ──────────────────────────────────────────────────────────────────
builder.Services.AddScoped<CollectionsService>();
builder.Services.AddScoped<SettingsService>();
builder.Services.AddScoped<PricingService>();
builder.Services.AddScoped<ProductsService>();
builder.Services.AddScoped<OrdersService>();
builder.Services.AddScoped<ExpensesService>();
builder.Services.AddScoped<IncomesService>();
builder.Services.AddScoped<FinancesService>();
builder.Services.AddScoped<TasksService>();
builder.Services.AddScoped<InvoicesService>();
builder.Services.AddScoped<InvoiceReadingService>();

// The AI reader is always registered; it reports itself unavailable until InvoiceReader:Provider/Model/ApiKey are set.
builder.Services.Configure<InvoiceReaderOptions>(builder.Configuration.GetSection(InvoiceReaderOptions.SectionName));
builder.Services.AddSingleton<IInvoiceAiProvider, GeminiInvoiceProvider>();
builder.Services.AddSingleton<IInvoiceAiProvider, OpenAiInvoiceProvider>();
builder.Services.AddSingleton<IInvoiceAiProvider, ClaudeInvoiceProvider>();
builder.Services.AddHttpClient<IInvoiceReader, AiInvoiceReader>(client => client.Timeout = TimeSpan.FromSeconds(30));

// The online store (Shopify): read-only. The domain, token and webhook secret are env vars.
builder.Services.Configure<ShopifyOptions>(builder.Configuration.GetSection(ShopifyOptions.SectionName));
builder.Services.AddHttpClient<IShopifyCatalogClient, ShopifyCatalogClient>(client => client.Timeout = TimeSpan.FromSeconds(30));
builder.Services.AddScoped<ShopifyImportService>();
builder.Services.AddScoped<ShopifyOrderImportService>();

// Invoice files stay on the disk of the server, outside the web root, until cloud storage is chosen.
var invoicesPath = builder.Configuration["Invoices:StoragePath"]
    ?? Path.Combine(builder.Environment.ContentRootPath, "App_Data", "invoices");
builder.Services.AddSingleton<IInvoiceStorage>(new LocalInvoiceStorage(invoicesPath));
builder.Services.AddScoped<UsersService>();
builder.Services.AddScoped<DbSeeder>();

// ── CORS ──────────────────────────────────────────────────────────────────────
// Allow the React client (running on a different port in dev) to call this API.
var clientOrigin = builder.Configuration["Cors:ClientOrigin"]
    ?? "http://localhost:5173";

builder.Services.AddCors(options =>
    options.AddDefaultPolicy(policy =>
        policy.WithOrigins(clientOrigin)
              .AllowAnyHeader()
              .AllowAnyMethod()));

// ─────────────────────────────────────────────────────────────────────────────
var app = builder.Build();

await app.InitializeDatabaseAsync();

// ── Middleware pipeline (ORDER MATTERS) ───────────────────────────────────────
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();   // Swagger JSON at /openapi/v1.json
    // In the cloud the platform's proxy terminates TLS and redirects HTTP itself.
    app.UseHttpsRedirection();
}
app.UseCors();

// FirebaseAuthMiddleware runs before any controller.
// It verifies the Firebase token and resolves the DB user.
app.UseMiddleware<FirebaseAuthMiddleware>();

app.MapControllers();

app.Run();
