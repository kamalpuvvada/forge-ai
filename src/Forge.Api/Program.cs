using System.Text.Json.Serialization;
using Forge.Core;
using Forge.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers()
    .AddJsonOptions(options => options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddCors(options => options.AddPolicy("LocalWeb", policy => policy
    .WithOrigins("http://localhost:5173", "https://localhost:5173")
    .AllowAnyHeader()
    .AllowAnyMethod()));

var configuredDataSource = builder.Configuration.GetValue<string>("Forge:DatabasePath") ?? "data/forge.db";
var databasePath = Path.GetFullPath(configuredDataSource, builder.Environment.ContentRootPath);
var connectionString = $"Data Source={databasePath}";

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<IClarificationEngine, FakeClarificationEngine>();
builder.Services.AddSingleton<IEngineeringTaskRepository>(_ => new SqliteEngineeringTaskRepository(connectionString));
builder.Services.AddSingleton(new SqliteDatabaseInitializer(connectionString));
builder.Services.AddScoped<EngineeringTaskService>();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    await app.Services.GetRequiredService<SqliteDatabaseInitializer>().InitializeAsync();
    app.UseSwagger();
    app.UseSwaggerUI();
}

if (!app.Environment.IsDevelopment()) app.UseHttpsRedirection();
app.UseCors("LocalWeb");
app.MapControllers();
app.Run();

public partial class Program;
