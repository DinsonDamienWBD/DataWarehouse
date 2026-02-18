using DataWarehouse.Dashboard.Hubs;
using DataWarehouse.Dashboard.Services;
using DataWarehouse.Dashboard.Security;
using DataWarehouse.Dashboard.Middleware;
using DataWarehouse.Dashboard.Validation;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.OpenApi;
using static Microsoft.OpenApi.ReferenceType;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container
builder.Services.AddRazorPages();
builder.Services.AddServerSideBlazor();
builder.Services.AddSignalR();
builder.Services.AddControllers()
    .AddValidation();

// Configure JWT Authentication
var jwtOptions = new JwtAuthenticationOptions();
builder.Configuration.GetSection(JwtAuthenticationOptions.SectionName).Bind(jwtOptions);

// Generate a secure key if not configured (development only)
if (string.IsNullOrEmpty(jwtOptions.SecretKey))
{
    if (builder.Environment.IsDevelopment())
    {
        // Use a development-only key
        jwtOptions.SecretKey = "DataWarehouse_Development_Secret_Key_At_Least_32_Chars!";
        builder.Services.AddSingleton(jwtOptions);
    }
    else
    {
        throw new InvalidOperationException(
            "JWT SecretKey must be configured in production. Set 'Authentication:Jwt:SecretKey' in configuration or environment variables.");
    }
}
else
{
    builder.Services.AddSingleton(jwtOptions);
}

builder.Services.AddSingleton<IJwtTokenService, JwtTokenService>();

builder.Services.AddAuthentication(options =>
{
    options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
    options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
})
.AddJwtBearer(options =>
{
    options.TokenValidationParameters = jwtOptions.GetTokenValidationParameters();
    options.Events = new JwtBearerEvents
    {
        OnAuthenticationFailed = context =>
        {
            var logger = context.HttpContext.RequestServices.GetRequiredService<ILogger<Program>>();
            logger.LogWarning("Authentication failed: {Message}", context.Exception.Message);
            return Task.CompletedTask;
        },
        OnTokenValidated = context =>
        {
            var logger = context.HttpContext.RequestServices.GetRequiredService<ILogger<Program>>();
            var username = context.Principal?.Identity?.Name;
            logger.LogDebug("Token validated for user: {Username}", username);
            return Task.CompletedTask;
        }
    };

    // Support tokens in query string for SignalR
    options.Events.OnMessageReceived = context =>
    {
        var accessToken = context.Request.Query["access_token"];
        var path = context.HttpContext.Request.Path;

        if (!string.IsNullOrEmpty(accessToken) && path.StartsWithSegments("/hubs"))
        {
            context.Token = accessToken;
        }

        return Task.CompletedTask;
    };
});

// Configure Authorization Policies
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy(AuthorizationPolicies.AdminOnly, policy =>
        policy.RequireRole(UserRoles.Admin));

    options.AddPolicy(AuthorizationPolicies.OperatorOrAdmin, policy =>
        policy.RequireRole(UserRoles.Admin, UserRoles.Operator));

    options.AddPolicy(AuthorizationPolicies.ReadOnly, policy =>
        policy.RequireRole(UserRoles.Admin, UserRoles.Operator, UserRoles.User, UserRoles.ReadOnly));

    options.AddPolicy(AuthorizationPolicies.Authenticated, policy =>
        policy.RequireAuthenticatedUser());
});

// Add Kernel host service (manages the DataWarehouse Kernel)
builder.Services.AddSingleton<KernelHostService>();
builder.Services.AddSingleton<IKernelHostService>(sp => sp.GetRequiredService<KernelHostService>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<KernelHostService>());

// Add dashboard services
builder.Services.AddSingleton<IPluginDiscoveryService, PluginDiscoveryService>();
builder.Services.AddSingleton<ISystemHealthService, SystemHealthService>();
builder.Services.AddSingleton<IStorageManagementService, StorageManagementService>();
builder.Services.AddSingleton<IAuditLogService, AuditLogService>();
builder.Services.AddSingleton<IConfigurationService, ConfigurationService>();
builder.Services.AddSingleton<DataWarehouse.Dashboard.Controllers.IBackupService, DataWarehouse.Dashboard.Controllers.InMemoryBackupService>();

// Add hosted services for background monitoring
builder.Services.AddHostedService<HealthMonitorService>();
builder.Services.AddHostedService<DashboardBroadcastService>();

// Configure Rate Limiting
builder.Services.AddRateLimiting(builder.Configuration);

// Configure CORS for API access (SECURITY: Do NOT use AllowAnyOrigin in production)
var corsOptions = new CorsOptions();
builder.Configuration.GetSection(CorsOptions.SectionName).Bind(corsOptions);

builder.Services.AddCors(options =>
{
    options.AddPolicy("ConfiguredOrigins", policy =>
    {
        if (corsOptions.AllowedOrigins.Length > 0)
        {
            policy.WithOrigins(corsOptions.AllowedOrigins)
                  .WithMethods(corsOptions.AllowedMethods)
                  .WithHeaders(corsOptions.AllowedHeaders)
                  .WithExposedHeaders(corsOptions.ExposedHeaders)
                  .SetPreflightMaxAge(TimeSpan.FromSeconds(corsOptions.PreflightMaxAge));

            if (corsOptions.AllowCredentials)
            {
                policy.AllowCredentials();
            }
        }
        else if (builder.Environment.IsDevelopment())
        {
            // Development fallback - still more restrictive than AllowAnyOrigin
            policy.WithOrigins("http://localhost:5000", "https://localhost:5001", "http://localhost:3000")
                  .WithMethods("GET", "POST", "PUT", "DELETE", "PATCH", "OPTIONS")
                  .WithHeaders("Content-Type", "Authorization", "X-Requested-With")
                  .AllowCredentials();
        }
        else
        {
            // Production without configured origins - very restrictive
            policy.WithOrigins("https://localhost")
                  .WithMethods("GET", "POST")
                  .WithHeaders("Content-Type", "Authorization");
        }
    });
});

// Add Swagger for API documentation
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new() { Title = "DataWarehouse Admin API", Version = "v1" });

    // Add JWT authentication to Swagger
    c.AddSecurityDefinition("Bearer", new Microsoft.OpenApi.OpenApiSecurityScheme
    {
        Description = "JWT Authorization header using the Bearer scheme. Enter 'Bearer' [space] and then your token.",
        Name = "Authorization",
        In = Microsoft.OpenApi.ParameterLocation.Header,
        Type = Microsoft.OpenApi.SecuritySchemeType.ApiKey,
        Scheme = "Bearer",
        BearerFormat = "JWT"
    });

    // Security requirement configuration - API changed in OpenApi 2.x
    // This would require updating to match the new API structure
    // c.AddSecurityRequirement(...);  // Commented out due to API compatibility
});

var app = builder.Build();

// Configure the HTTP request pipeline
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

// Add security headers middleware
app.Use(async (context, next) =>
{
    context.Response.Headers.Append("X-Content-Type-Options", "nosniff");
    context.Response.Headers.Append("X-Frame-Options", "DENY");
    context.Response.Headers.Append("Referrer-Policy", "strict-origin-when-cross-origin");

    // S7039: CSP requires unsafe-inline/unsafe-eval for Blazor Server + SignalR WebSocket support
    // This is the most restrictive CSP compatible with Blazor Server architecture
    #pragma warning disable S7039
    context.Response.Headers.Append("Content-Security-Policy",
        "default-src 'self'; " +
        "script-src 'self' 'unsafe-inline' 'unsafe-eval'; " +  // Blazor requires unsafe-inline/eval
        "style-src 'self' 'unsafe-inline'; " +  // Blazor requires unsafe-inline
        "img-src 'self' data: https:; " +
        "font-src 'self' data:; " +
        "connect-src 'self' ws: wss:; " +  // SignalR requires websocket
        "frame-ancestors 'none'; " +
        "base-uri 'self'; " +
        "form-action 'self'");
    #pragma warning restore S7039

    await next();
});

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseRouting();
app.UseCors("ConfiguredOrigins");

// Rate limiting middleware (before authentication for early rejection)
app.UseRateLimiting();

// Authentication & Authorization middleware
app.UseAuthentication();
app.UseAuthorization();

// Enable Swagger
app.UseSwagger();
app.UseSwaggerUI(c => c.SwaggerEndpoint("/swagger/v1/swagger.json", "DataWarehouse Admin API v1"));

app.MapBlazorHub();
app.MapHub<DashboardHub>("/hubs/dashboard");
app.MapControllers();
app.MapFallbackToPage("/_Host");

app.Run();
