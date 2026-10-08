using AnalyzerWebApi.Agents;
using AnalyzerWebApi.Authentication;
using AnalyzerWebApi.Services;
using Azure.Identity;
using Microsoft.Agents.Authentication.Msal;
using Microsoft.Agents.Builder;
using Microsoft.Agents.Hosting.AspNetCore;
using Microsoft.Agents.Storage;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Graph;
using Microsoft.Identity.Web;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddMicrosoftIdentityWebApi(builder.Configuration.GetSection("AzureAd"));

// Add Graph service
builder.Services.AddScoped(sp =>
{
    var options = new TokenCredentialOptions
    {
        AuthorityHost = AzureAuthorityHosts.AzurePublicCloud
    };

    var clientSecret = builder.Configuration["AzureAd:ClientSecret"];
    var tenantId = builder.Configuration["AzureAd:TenantId"];
    var clientId = builder.Configuration["AzureAd:ClientId"];

    var clientSecretCredential = new ClientSecretCredential(
        tenantId, clientId, clientSecret, options);

    var graphScopes = builder.Configuration.GetSection("GraphInfo:Scopes").Get<string[]>();

    return new GraphServiceClient(clientSecretCredential, graphScopes);
});

// Add Foundry services
builder.Services.AddScoped<IFoundryServices, FoundryServices>(sp =>
{
    var endpoint = builder.Configuration["MicrosoftFoundry:Endpoint"] ?? throw new InvalidOperationException("MicrosoftFoundry:Endpoint is not configured.");
    var agentName = builder.Configuration["MicrosoftFoundry:AgentName"] ?? throw new InvalidOperationException("MicrosoftFoundry:AgentName is not configured.");
    var logger = sp.GetRequiredService<ILogger<FoundryServices>>();

    return new FoundryServices(endpoint, agentName, logger);
});

// Bot Teams : validation des appels entrants et identité des réponses.
builder.Services.AddTeamsBotAuthentication(builder.Configuration);
builder.Services.AddHttpClient();
builder.Services.AddDefaultMsalAuth(builder.Configuration);
builder.AddAgentApplicationOptions();
builder.AddAgent<TeamsDemoAgent>();
builder.Services.AddSingleton<IStorage, MemoryStorage>();

builder.Services.AddControllers();
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
// builder.Services.AddOpenApi();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    //app.MapOpenApi();
}

app.UseHttpsRedirection();

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();

app.MapPost("/api/messages", async (
    HttpRequest request,
    HttpResponse response,
    IAgentHttpAdapter adapter,
    IAgent agent,
    CancellationToken cancellationToken) =>
{
    await adapter.ProcessAsync(request, response, agent, cancellationToken);
}).RequireAuthorization(TeamsBotAuthentication.Policy);

app.Run();
