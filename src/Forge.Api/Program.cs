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
aiOptions.ValidateSyntax();
var verificationLanguageOverridePolicy = new VerificationPlanLanguageOverridePolicy(
    builder.Environment.IsDevelopment() &&
    string.Equals(aiOptions.Mode, ForgeAiModes.OpenAI, StringComparison.OrdinalIgnoreCase) &&
    builder.Configuration.GetValue<bool>("Forge:Verification:AllowInitialPlanLanguageOverride"));
var analysisLimits = builder.Configuration.GetSection("Forge:RepositoryAnalysis").Get<RepositoryAnalysisLimits>() ?? new RepositoryAnalysisLimits();
var implementationLimits = builder.Configuration.GetSection("Forge:Implementation:Limits").Get<ImplementationLimits>() ?? new ImplementationLimits();
var verificationLimits = builder.Configuration.GetSection("Forge:Verification:Limits").Get<VerificationLimits>() ?? new VerificationLimits();
var correctionLimits = builder.Configuration.GetSection("Forge:Correction:Limits").Get<CorrectionLimits>() ?? new CorrectionLimits();
var workspaceOptions = builder.Configuration.GetSection("Forge:Implementation").Get<ImplementationWorkspaceOptions>() ?? new ImplementationWorkspaceOptions();
if (!Path.IsPathFullyQualified(workspaceOptions.WorktreeRoot))
    workspaceOptions.WorktreeRoot = Path.GetFullPath(workspaceOptions.WorktreeRoot, builder.Environment.ContentRootPath);
var gitProcessOptions = builder.Configuration.GetSection("Forge:Implementation:Git").Get<GitProcessOptions>() ?? new GitProcessOptions();
gitProcessOptions.OwnedRoot = workspaceOptions.WorktreeRoot;
gitProcessOptions.HooksDirectory = Path.Combine(workspaceOptions.WorktreeRoot, ".empty-hooks");
gitProcessOptions.SafeHomeDirectory = Path.Combine(workspaceOptions.WorktreeRoot, ".git-home");
var deliveryProcessOptions = builder.Configuration.GetSection("Forge:Delivery:Process").Get<DeliveryProcessOptions>() ?? new DeliveryProcessOptions();
deliveryProcessOptions.WorktreeRoot = workspaceOptions.WorktreeRoot;
deliveryProcessOptions.HooksDirectory = Path.Combine(workspaceOptions.WorktreeRoot, ".delivery-hooks");
var apiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(aiOptions);
builder.Services.AddSingleton(analysisLimits);
builder.Services.AddSingleton(implementationLimits);
builder.Services.AddSingleton(verificationLimits);
builder.Services.AddSingleton(verificationLanguageOverridePolicy);
builder.Services.AddSingleton(correctionLimits);
builder.Services.AddSingleton(workspaceOptions);
builder.Services.AddSingleton(gitProcessOptions);
builder.Services.AddSingleton(deliveryProcessOptions);
builder.Services.AddSingleton<ImplementationOperationCoordinator>();
builder.Services.AddSingleton(new ImplementationProcessIdentity(Guid.NewGuid()));
builder.Services.AddSingleton(new OpenAIConfigurationState(!string.IsNullOrWhiteSpace(apiKey)));
builder.Services.AddSingleton(new ModelCostCalculator(aiOptions.Pricing));
builder.Services.AddSingleton<ModelCostResolver>();
if (!string.IsNullOrWhiteSpace(apiKey))
    builder.Services.AddSingleton<IOpenAIResponsesGateway>(new SdkOpenAIResponsesGateway(apiKey));
builder.Services.AddSingleton<IEngineeringTaskPdfExporter, TaskPdfExporter>();
builder.Services.AddSingleton<IImplementationPlanPdfExporter, ImplementationPlanPdfExporter>();
builder.Services.AddSingleton<IVerificationPlanPdfExporter, VerificationPlanPdfExporter>();
builder.Services.AddSingleton<IClarificationEngine>(services => aiOptions.Mode switch
{
    ForgeAiModes.Fake => new FakeClarificationEngine(),
    ForgeAiModes.OpenAI => new OpenAIClarificationEngine(
        aiOptions,
        services.GetService<IOpenAIResponsesGateway>(),
        services.GetRequiredService<ModelCostCalculator>(),
        services.GetRequiredService<TimeProvider>()),
    _ => throw new InvalidOperationException($"Unsupported Forge AI mode '{aiOptions.Mode}'. Use 'Fake' or 'OpenAI'.")
});
builder.Services.AddSingleton<IPlanningEngine>(services => aiOptions.Mode switch
{
    ForgeAiModes.Fake => new FakePlanningEngine(),
    ForgeAiModes.OpenAI => new OpenAIPlanningEngine(
        aiOptions,
        services.GetService<IOpenAIResponsesGateway>(),
        services.GetRequiredService<ModelCostCalculator>(),
        services.GetRequiredService<TimeProvider>()),
    _ => throw new InvalidOperationException($"Unsupported Forge AI mode '{aiOptions.Mode}'. Use 'Fake' or 'OpenAI'.")
});
builder.Services.AddSingleton<IImplementationEngine>(services => aiOptions.Mode switch
{
    ForgeAiModes.Fake => new FakeImplementationEngine(),
    ForgeAiModes.OpenAI => new OpenAIImplementationEngine(
        aiOptions,
        services.GetService<IOpenAIResponsesGateway>(),
        services.GetRequiredService<ModelCostCalculator>(),
        services.GetRequiredService<TimeProvider>()),
    _ => throw new InvalidOperationException($"Unsupported Forge AI mode '{aiOptions.Mode}'. Use 'Fake' or 'OpenAI'.")
});
builder.Services.AddSingleton<IVerificationPlanEngine>(services => aiOptions.Mode switch
{
    ForgeAiModes.Fake => new FakeVerificationPlanEngine(),
    ForgeAiModes.OpenAI => new OpenAIVerificationPlanEngine(
        aiOptions,
        services.GetService<IOpenAIResponsesGateway>(),
        services.GetRequiredService<ModelCostCalculator>(),
        services.GetRequiredService<TimeProvider>(),
        verificationLanguageOverridePolicy.Enabled),
    _ => throw new InvalidOperationException($"Unsupported Forge AI mode '{aiOptions.Mode}'. Use 'Fake' or 'OpenAI'.")
});
builder.Services.AddSingleton<IFailureAnalysisEngine>(services => aiOptions.Mode switch
{
    ForgeAiModes.Fake => new FakeFailureAnalysisEngine(),
    ForgeAiModes.OpenAI => new OpenAIFailureAnalysisEngine(
        aiOptions,
        services.GetService<IOpenAIResponsesGateway>(),
        services.GetRequiredService<ModelCostCalculator>(),
        services.GetRequiredService<TimeProvider>(),
        services.GetRequiredService<CorrectionLimits>()),
    _ => throw new InvalidOperationException($"Unsupported Forge AI mode '{aiOptions.Mode}'. Use 'Fake' or 'OpenAI'.")
});
builder.Services.AddSingleton(services => new SqliteEngineeringTaskRepository(
    connectionString, implementationLimits, services.GetRequiredService<TimeProvider>(), verificationLimits,
    correctionLimits));
builder.Services.AddSingleton<IEngineeringTaskRepository>(services =>
    services.GetRequiredService<SqliteEngineeringTaskRepository>());
builder.Services.AddSingleton<IImplementationApprovalRepository>(services =>
    services.GetRequiredService<SqliteEngineeringTaskRepository>());
builder.Services.AddSingleton<IVerificationRepository>(services =>
    services.GetRequiredService<SqliteEngineeringTaskRepository>());
builder.Services.AddSingleton<ICorrectionWorkflowRepository>(services =>
    services.GetRequiredService<SqliteEngineeringTaskRepository>());
builder.Services.AddSingleton<IDeliveryRepository>(services =>
    services.GetRequiredService<SqliteEngineeringTaskRepository>());
builder.Services.AddSingleton<IRepositoryDiscoveryService, RepositoryDiscoveryService>();
builder.Services.AddSingleton<IEvidenceSelectionService, DeterministicEvidenceSelectionService>();
builder.Services.AddSingleton<RepositoryFileSafetyPolicy>();
builder.Services.AddSingleton<IGitExecutablePathResolver, GitExecutablePathResolver>();
builder.Services.AddSingleton<IGitProcessRunner, GitProcessRunner>();
builder.Services.AddSingleton<IDeliveryProcessRunner, DeliveryProcessRunner>();
builder.Services.AddSingleton<IDeliveryExecutableAvailability, DeliveryExecutableAvailability>();
builder.Services.AddSingleton<IDeliveryGitClient, GitHubDeliveryClient>();
builder.Services.AddSingleton<IGitHubCliClient, GitHubCliClient>();
builder.Services.AddSingleton<IImplementationWorkspaceManager, GitWorktreeManager>();
builder.Services.AddSingleton(new SqliteDatabaseInitializer(connectionString));
builder.Services.AddScoped<EngineeringTaskService>();
builder.Services.AddScoped<ImplementationApprovalService>();
builder.Services.AddScoped<VerificationWorkflowService>();
builder.Services.AddScoped<CorrectionWorkflowService>();
builder.Services.AddScoped<CorrectionImplementationService>();
builder.Services.AddScoped<DeliveryService>();

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
