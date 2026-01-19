using DataWarehouse.Dashboard.Hubs;
using DataWarehouse.Dashboard.Services;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container
builder.Services.AddRazorPages();
builder.Services.AddServerSideBlazor();
builder.Services.AddSignalR();
builder.Services.AddControllers();

// Add dashboard services
builder.Services.AddSingleton<IPluginDiscoveryService, PluginDiscoveryService>();
builder.Services.AddSingleton<ISystemHealthService, SystemHealthService>();
builder.Services.AddSingleton<IStorageManagementService, StorageManagementService>();
builder.Services.AddSingleton<IAuditLogService, AuditLogService>();
builder.Services.AddSingleton<IConfigurationService, ConfigurationService>();

// Add hosted services for background monitoring
builder.Services.AddHostedService<HealthMonitorService>();
builder.Services.AddHostedService<DashboardBroadcastService>();

// Configure CORS for API access
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAll", policy =>
    {
        policy.AllowAnyOrigin()
              .AllowAnyMethod()
              .AllowAnyHeader();
    });
});

// Add Swagger for API documentation
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new() { Title = "DataWarehouse Admin API", Version = "v1" });
});

var app = builder.Build();

// Configure the HTTP request pipeline
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseRouting();
app.UseCors("AllowAll");

// Enable Swagger
app.UseSwagger();
app.UseSwaggerUI(c => c.SwaggerEndpoint("/swagger/v1/swagger.json", "DataWarehouse Admin API v1"));

app.MapBlazorHub();
app.MapHub<DashboardHub>("/hubs/dashboard");
app.MapControllers();
app.MapFallbackToPage("/_Host");

app.Run();
