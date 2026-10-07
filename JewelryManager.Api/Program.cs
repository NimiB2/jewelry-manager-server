using FirebaseAdmin;
using Google.Apis.Auth.OAuth2;
using JewelryManager.Api.Auth;
using JewelryManager.Api.Common.Filters;
using JewelryManager.Api.Data;
using JewelryManager.Api.Features.Collections;
using JewelryManager.Api.Features.Settings;
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
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("Default")));

// ── Firebase ──────────────────────────────────────────────────────────────────
// Initialize the Firebase Admin SDK once (singleton for the app lifetime).
// The credential file path comes from an environment variable — never hardcoded.
var firebaseCredentialPath = builder.Configuration["Firebase:CredentialPath"]
    ?? throw new InvalidOperationException("Firebase:CredentialPath is not configured.");

// GoogleCredential.FromFile is flagged as deprecated but remains the correct
// approach for loading service account JSON files in FirebaseAdmin 3.x.
// The suggested CredentialFactory alternative is not yet available in this SDK version.
#pragma warning disable CS0618
FirebaseApp.Create(new AppOptions
{
    Credential = GoogleCredential.FromFile(firebaseCredentialPath),
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

await app.InitializeDevelopmentDatabaseAsync();

// ── Middleware pipeline (ORDER MATTERS) ───────────────────────────────────────
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();   // Swagger JSON at /openapi/v1.json
}

app.UseHttpsRedirection();
app.UseCors();

// FirebaseAuthMiddleware runs before any controller.
// It verifies the Firebase token and resolves the DB user.
app.UseMiddleware<FirebaseAuthMiddleware>();

app.MapControllers();

app.Run();
