using System.Text.Json.Serialization;
using Forge.Api;
using Forge.Api.Controllers;
using Forge.Core;
using Forge.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers()
    .AddJsonOptions(options => options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<ForgeExceptionHandler>();
builder.Services.AddCors(options => options.AddPolicy("LocalWeb", policy => policy
    .WithOrigins("http://localhost:5173", "http://127.0.0.1:5173", "https://localhost:5173")
    .AllowAnyHeader()
    .AllowAnyMethod()));

var configuredDataSource = builder.Configuration.GetValue<string>("Forge:DatabasePath") ?? "data/forge.db";
var databasePath = Path.GetFullPath(configuredDataSource, builder.Environment.ContentRootPath);
var connectionString = $"Data Source={databasePath}";
var aiOptions = builder.Configuration.GetSection("Forge:AI").Get<ForgeAiOptions>() ?? new ForgeAiOptions();
var apiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(aiOptions);
builder.Services.AddSingleton(new OpenAIConfigurationState(!string.IsNullOrWhiteSpace(apiKey)));
builder.Services.AddSingleton(new ModelCostCalculator(aiOptions.Pricing));
builder.Services.AddSingleton<IClarificationEngine>(services => aiOptions.Mode switch
{
    ForgeAiModes.Fake => new FakeClarificationEngine(),
    ForgeAiModes.OpenAI => new OpenAIClarificationEngine(
        aiOptions,
        string.IsNullOrWhiteSpace(apiKey) ? null : new SdkOpenAIResponsesGateway(apiKey),
        services.GetRequiredService<ModelCostCalculator>(),
        services.GetRequiredService<TimeProvider>()),
    _ => throw new InvalidOperationException($"Unsupported Forge AI mode '{aiOptions.Mode}'. Use 'Fake' or 'OpenAI'.")
});
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

app.UseExceptionHandler();
if (!app.Environment.IsDevelopment()) app.UseHttpsRedirection();
app.UseCors("LocalWeb");
app.MapControllers();
app.Run();

public partial class Program;
