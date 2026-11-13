using System.Net;
using System.Security.Claims;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Mvc;
using Microsoft.OpenApi;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole();
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 7 * 1024 * 1024);
builder.Services.AddSingleton<ForgeStore>();
builder.Services.AddSingleton<RepositoryLocks>();
builder.Services.AddSingleton<GitProcess>();
builder.Services.AddSingleton<GitRepository>();
builder.Services.AddSingleton<GitTransport>();
builder.Services.AddOpenApi(options =>
{
    options.AddDocumentTransformer((document, _, _) =>
    {
        document.Servers = [new OpenApiServer { Url = "/" }];
        document.Components ??= new();
        document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
        document.Components.SecuritySchemes["session"] = new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.ApiKey,
            In = ParameterLocation.Cookie,
            Name = "mingo.session"
        };
        return Task.CompletedTask;
    });
    options.AddOperationTransformer((operation, context, _) =>
    {
        var action = context.Description.ActionDescriptor;
        if (action.EndpointMetadata.OfType<Microsoft.AspNetCore.Authorization.IAuthorizeData>().Any())
        {
            operation.Security = [new OpenApiSecurityRequirement { [new OpenApiSecuritySchemeReference("session")] = [] }];
        }
        if (context.Description.RelativePath?.StartsWith("api/v1/", StringComparison.Ordinal) == true &&
            !HttpMethods.IsGet(context.Description.HttpMethod ?? ""))
        {
            operation.Description = "Send the X-CSRF-Token header returned by GET /api/v1/session.";
        }
        return Task.CompletedTask;
    });
});
builder.Services.AddAntiforgery(options =>
{
    options.HeaderName = "X-CSRF-Token";
    options.Cookie.SecurePolicy = builder.Environment.IsDevelopment() ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
});
var dataPath = Path.GetFullPath(builder.Configuration["Forge:DataPath"] ?? "data", builder.Environment.ContentRootPath);
builder.Services.AddDataProtection().SetApplicationName("MinGo.GitServer")
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(dataPath, "keys")));
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(options =>
{
    options.Cookie.Name = "mingo.session";
    options.Cookie.HttpOnly = true;
    options.Cookie.SameSite = SameSiteMode.Strict;
    options.Cookie.SecurePolicy = builder.Environment.IsDevelopment() ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
    options.ExpireTimeSpan = TimeSpan.FromHours(12);
    options.Events.OnRedirectToLogin = context =>
    {
        context.Response.StatusCode = 401;
        return Task.CompletedTask;
    };
});
builder.Services.AddAuthorization();
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = 429;
    options.AddPolicy("login", _ => RateLimitPartition.GetFixedWindowLimiter("admin", _ => new()
    {
        PermitLimit = 10, Window = TimeSpan.FromMinutes(1), QueueLimit = 0
    }));
});
if (builder.Configuration["Forge:TrustedProxy"] is { } proxy)
{
    builder.Services.Configure<ForwardedHeadersOptions>(options =>
    {
        options.ForwardedHeaders = ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedFor;
        options.KnownIPNetworks.Clear();
        options.KnownProxies.Clear();
        options.KnownProxies.Add(IPAddress.Parse(proxy));
        options.ForwardLimit = 1;
    });
}

var app = builder.Build();
if (builder.Configuration["Forge:TrustedProxy"] is not null)
    app.UseForwardedHeaders();
var store = app.Services.GetRequiredService<ForgeStore>();
using var dataLock = store.LockDataDirectory();
store.Initialize(builder.Configuration["Forge:AdminPassword"]);
var git = app.Services.GetRequiredService<GitProcess>();
await git.Run(null, ["--version"], CancellationToken.None);
app.Use(async (http, next) =>
{
    http.Response.Headers["X-Content-Type-Options"] = "nosniff";
    http.Response.Headers["Referrer-Policy"] = "no-referrer";
    if (http.Request.Path.StartsWithSegments("/api"))
        http.Response.Headers.CacheControl = "no-store";
    try
    {
        if (!app.Environment.IsDevelopment() && !http.Request.IsHttps && http.Request.Path != "/health")
            throw new ForgeException(400, "HTTPS is required.");
        await next(http);
    }
    catch (Exception error)
    {
        if (http.RequestAborted.IsCancellationRequested || http.Response.HasStarted)
        {
            http.Abort();
            return;
        }
        var status = error switch
        {
            ForgeException forge => forge.Status,
            BadHttpRequestException bad => bad.StatusCode,
            AntiforgeryValidationException => 400,
            _ => 500
        };
        if (status >= 500)
            app.Logger.LogError(error, "Request failed with HTTP {Status}", status);
        var message = error is ForgeException ? error.Message : status == 500 ? "Request failed." : "Invalid request.";
        if (http.Request.Path.StartsWithSegments("/git"))
        {
            http.Response.StatusCode = status;
            return;
        }
        await Results.Problem(detail: message, statusCode: status).ExecuteAsync(http);
    }
});
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/health", () => TypedResults.Ok(new { status = "ok" }));
var openApi = app.MapOpenApi();
if (!app.Environment.IsDevelopment())
    openApi.RequireAuthorization();
var api = app.MapGroup("/api/v1");
api.AddEndpointFilter(async (context, next) =>
{
    if (!HttpMethods.IsGet(context.HttpContext.Request.Method))
        await context.HttpContext.RequestServices.GetRequiredService<IAntiforgery>().ValidateRequestAsync(context.HttpContext);
    return await next(context);
});
api.MapGet("/session", (HttpContext http, IAntiforgery antiforgery) => TypedResults.Ok(new SessionInfo(
    http.User.Identity?.Name, antiforgery.GetAndStoreTokens(http).RequestToken!))).WithName("getSession");
api.MapPost("/session", async (LoginRequest request, HttpContext http) =>
{
    if (request.Username is null || request.Password is null || !store.CheckPassword(request.Username, request.Password))
        return Results.Problem(statusCode: 401, detail: "Invalid username or password.");
    var identity = new ClaimsIdentity([new Claim(ClaimTypes.Name, store.Admin), new Claim(ClaimTypes.NameIdentifier, "1")],
        CookieAuthenticationDefaults.AuthenticationScheme);
    await http.SignInAsync(new ClaimsPrincipal(identity));
    return Results.NoContent();
}).RequireRateLimiting("login").Produces(204).ProducesProblem(401).WithName("login");

var admin = api.MapGroup("").RequireAuthorization();
admin.MapDelete("/session", async (HttpContext http) =>
{
    await http.SignOutAsync();
    return TypedResults.NoContent();
}).WithName("logout");
admin.MapPost("/git-token", () => TypedResults.Ok(store.IssueToken())).WithName("issueGitToken");
var repos = admin.MapGroup("/repos");
repos.MapGet("", () => TypedResults.Ok(store.List().Select(Present).ToArray())).WithName("listRepositories");
repos.MapPost("", async (CreateRepository request, GitRepository service, CancellationToken token) =>
{
    var repository = await service.Create(request, token);
    return TypedResults.Created($"/api/v1/repos/{repository.Id}", Present(repository));
}).WithName("createRepository");
var repo = repos.MapGroup("/{id:guid}");
repo.MapGet("", (Guid id, GitRepository service) => TypedResults.Ok(Present(service.Get(id)))).WithName("getRepository");
repo.MapDelete("", (Guid id, GitRepository service) =>
{
    service.Delete(id);
    return TypedResults.NoContent();
}).WithName("deleteRepository");
MapReferences(repo, "branches", "heads", "Branch");
MapReferences(repo, "tags", "tags", "Tag");
repo.MapGet("/commits", async (Guid id, string? branch, string? tag, int? skip, int? count, GitRepository service, CancellationToken token) =>
    TypedResults.Ok(await service.History(id, branch, tag, skip, count, token))).WithName("listCommits");
repo.MapGet("/commits/{sha}", async (Guid id, string sha, GitRepository service, CancellationToken token) =>
    TypedResults.Ok(await service.Commit(id, sha, token))).WithName("getCommit");
repo.MapGet("/diff", async (Guid id, string sha, string? path, GitRepository service, CancellationToken token) =>
    TypedResults.Ok(await service.Diff(id, sha, path, token))).WithName("getDiff");
repo.MapGet("/tree", async (Guid id, string? branch, string? tag, string? path, string? sha, GitRepository service, CancellationToken token) =>
    TypedResults.Ok(await service.Tree(id, branch, tag, path, sha, token))).WithName("getTree");
repo.MapGet("/blob", async (Guid id, string? branch, string? tag, string path, string? sha, GitRepository service, CancellationToken token) =>
    TypedResults.Ok(await service.Blob(id, branch, tag, path, sha, token))).WithName("getBlob");
repo.MapPut("/files", async (Guid id, FileChange request, GitRepository service, CancellationToken token) =>
    TypedResults.Ok(await service.Edit(id, request, false, token))).WithName("writeFile")
    .ProducesProblem(409).ProducesProblem(413).ProducesProblem(422);
repo.MapDelete("/files", async (Guid id, [FromBody] FileChange request, GitRepository service, CancellationToken token) =>
    TypedResults.Ok(await service.Edit(id, request, true, token))).WithName("deleteFile")
    .ProducesProblem(404).ProducesProblem(409).ProducesProblem(422);

app.MapMethods("/git/{owner}/{name}.git/{**endpoint}", ["GET", "POST"],
    (HttpContext http, string owner, string name, string endpoint, GitTransport transport) => transport.Handle(http, owner, name, endpoint))
    .ExcludeFromDescription();
app.Run();

RepositoryInfo Present(Repository repository) => new(repository.Id, repository.Name, repository.DefaultBranch,
    repository.CreatedAt, $"/git/{store.Admin}/{repository.Name}.git");

static void MapReferences(RouteGroupBuilder group, string route, string kind, string label)
{
    group.MapGet($"/{route}", async (Guid id, GitRepository service, CancellationToken token) =>
        TypedResults.Ok(await service.References(id, kind, token))).WithName($"list{label}Refs");
    group.MapPost($"/{route}", async (Guid id, CreateReference request, GitRepository service, CancellationToken token) =>
        TypedResults.Ok(await service.AddReference(id, kind, request, token))).WithName($"create{label}");
    group.MapDelete($"/{route}", async (Guid id, string name, GitRepository service, CancellationToken token) =>
    {
        await service.RemoveReference(id, kind, name, token);
        return TypedResults.NoContent();
    }).WithName($"delete{label}");
}

record SessionInfo(string? Username, string CsrfToken);
record LoginRequest(string Username, string Password);
record RepositoryInfo(Guid Id, string Name, string DefaultBranch, string CreatedAt, string ClonePath);
