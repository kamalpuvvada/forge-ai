using System.Text.Json.Serialization;
using Forge.Api;
using Forge.Api.Controllers;
using Forge.Core;
using Forge.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddScoped<EngineeringTaskNotFoundExceptionFilter>();
builder.Services.AddControllers(options => options.Filters.AddService<EngineeringTaskNotFoundExceptionFilter>())
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
var analysisLimits = builder.Configuration.GetSection("Forge:RepositoryAnalysis").Get<RepositoryAnalysisLimits>() ?? new RepositoryAnalysisLimits();
var implementationLimits = builder.Configuration.GetSection("Forge:Implementation:Limits").Get<ImplementationLimits>() ?? new ImplementationLimits();
var workspaceOptions = builder.Configuration.GetSection("Forge:Implementation").Get<ImplementationWorkspaceOptions>() ?? new ImplementationWorkspaceOptions();
if (!Path.IsPathFullyQualified(workspaceOptions.WorktreeRoot))
    workspaceOptions.WorktreeRoot = Path.GetFullPath(workspaceOptions.WorktreeRoot, builder.Environment.ContentRootPath);
var gitProcessOptions = builder.Configuration.GetSection("Forge:Implementation:Git").Get<GitProcessOptions>() ?? new GitProcessOptions();
gitProcessOptions.ExecutablePath = GitExecutableResolver.Resolve(gitProcessOptions.ExecutablePath);
gitProcessOptions.OwnedRoot = workspaceOptions.WorktreeRoot;
gitProcessOptions.HooksDirectory = Path.Combine(workspaceOptions.WorktreeRoot, ".empty-hooks");
gitProcessOptions.SafeHomeDirectory = Path.Combine(workspaceOptions.WorktreeRoot, ".git-home");
var apiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(aiOptions);
builder.Services.AddSingleton(analysisLimits);
builder.Services.AddSingleton(implementationLimits);
builder.Services.AddSingleton(workspaceOptions);
builder.Services.AddSingleton(gitProcessOptions);
builder.Services.AddSingleton<ImplementationOperationCoordinator>();
builder.Services.AddSingleton(new ImplementationProcessIdentity(Guid.NewGuid()));
builder.Services.AddSingleton(new OpenAIConfigurationState(!string.IsNullOrWhiteSpace(apiKey)));
builder.Services.AddSingleton(new ModelCostCalculator(aiOptions.Pricing));
builder.Services.AddSingleton<ModelCostResolver>();
builder.Services.AddSingleton<IEngineeringTaskPdfExporter, TaskPdfExporter>();
builder.Services.AddSingleton<IImplementationPlanPdfExporter, ImplementationPlanPdfExporter>();
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
builder.Services.AddSingleton<IPlanningEngine>(services => aiOptions.Mode switch
{
    ForgeAiModes.Fake => new FakePlanningEngine(),
    ForgeAiModes.OpenAI => new OpenAIPlanningEngine(
        aiOptions,
        string.IsNullOrWhiteSpace(apiKey) ? null : new SdkOpenAIResponsesGateway(apiKey),
        services.GetRequiredService<ModelCostCalculator>(),
        services.GetRequiredService<TimeProvider>()),
    _ => throw new InvalidOperationException($"Unsupported Forge AI mode '{aiOptions.Mode}'. Use 'Fake' or 'OpenAI'.")
});
if (string.Equals(aiOptions.Mode, ForgeAiModes.Fake, StringComparison.OrdinalIgnoreCase))
    builder.Services.AddSingleton<IImplementationEngine, FakeImplementationEngine>();
builder.Services.AddSingleton<IEngineeringTaskRepository>(services => new SqliteEngineeringTaskRepository(
    connectionString, implementationLimits, services.GetRequiredService<TimeProvider>()));
builder.Services.AddSingleton<IRepositoryDiscoveryService, RepositoryDiscoveryService>();
builder.Services.AddSingleton<IEvidenceSelectionService, DeterministicEvidenceSelectionService>();
builder.Services.AddSingleton<RepositoryFileSafetyPolicy>();
builder.Services.AddSingleton<IGitProcessRunner, GitProcessRunner>();
builder.Services.AddSingleton<IImplementationWorkspaceManager, GitWorktreeManager>();
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
