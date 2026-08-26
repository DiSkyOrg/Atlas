using System.Globalization;
using System.Text;
using System.Threading.RateLimiting;
using BlazorBlueprint.Components;
using DiSkyAtlas.Components;
using DiSkyAtlas.Components.Docs;
using DiSkyAtlas.Endpoints;
using DiSkyAtlas.Services;
using DiSkyAtlas.Services.Ai;
using DiSkyAtlas.Services.Docs;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

// The UI is English-only and Blazor Blueprint formats floating-element positions with the
// thread culture (a French host yields "left: 1158,09px", which the browser ignores), so
// pin every circuit to the invariant culture.
CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.InvariantCulture;

var builder = WebApplication.CreateBuilder(args);

// Kestrel sits behind nginx: honour X-Forwarded-For/-Proto so the app knows the
// real scheme (HSTS is only emitted on requests it believes are HTTPS) and logs
// real client IPs. The app is only reachable through the proxy (the compose port
// binding), so the known-proxy allowlist is cleared: inside Docker the proxy
// connects from the bridge gateway, not loopback.
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.KnownNetworks.Clear();
    options.KnownProxies.Clear();
});

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents(options =>
    {
        // Doc readers who lose their circuit reload rather than resume: retaining
        // 100 disconnected circuits for 3 min (the defaults) just holds memory.
        options.DisconnectedCircuitMaxRetained = 40;
        options.DisconnectedCircuitRetentionPeriod = TimeSpan.FromMinutes(2);
    });

// Blazor Blueprint UI (styled components + headless primitives + services).
builder.Services.AddBlazorBlueprintComponents();

// The DiSky syntax manifest (loads wwwroot/data/atlas.json once).
builder.Services.AddSingleton<ManifestService>();

// Doc constructs → Blazor components. Adding a construct = one component + one line here.
builder.Services.AddSingleton(_ => new DocComponentRegistry()
    .RegisterDirective("syntax", typeof(SyntaxRefCard), "compact", "standard", "full")
    .RegisterDirective("entity", typeof(SyntaxRefCard), "compact", "standard", "full")
    .RegisterDirective("event", typeof(SyntaxRefCard), "compact", "standard", "full")
    .RegisterDirective("doc", typeof(DocRefCard))
    .RegisterDirective("toggle", typeof(DocToggle))
    .RegisterContainer("steps", typeof(DocSteps))
    .RegisterContainer("when", typeof(DocWhen)));

// The hand-written documentation pages (parses Docs/**/*.md once; hot-reloads in Development).
builder.Services.AddSingleton<DocsService>();

// Fuzzy ranking shared by the ⌘K palette and the agent API.
builder.Services.AddSingleton<SearchService>();

// The AI assistant (/api/v1/ask): OpenRouter-backed tool loop over the same in-memory data.
// Everything in the "Ai" section is hot-reloadable; the API key comes from OPENROUTER_API_KEY.
builder.Services.Configure<AiOptions>(builder.Configuration.GetSection(AiOptions.Section));
builder.Services.AddHttpClient(AskService.HttpClientName, client =>
{
    client.BaseAddress = new Uri(Environment.GetEnvironmentVariable("OPENROUTER_BASE_URL")
                                 ?? "https://openrouter.ai/api/v1/");
    client.Timeout = TimeSpan.FromSeconds(90);
    // OpenRouter attribution headers (app ranking / dashboards).
    client.DefaultRequestHeaders.Add("HTTP-Referer", "https://atlas.disky.me");
    client.DefaultRequestHeaders.Add("X-Title", "DiSky Atlas");
});
builder.Services.AddSingleton<AiBudget>();
builder.Services.AddSingleton<AiChatLog>();
builder.Services.AddSingleton<AiIpQuota>();
builder.Services.AddSingleton<AskService>();
builder.Services.AddSingleton<AskGuards>();
// The /ask page reads the client IP for its quota key (falls back to a per-circuit id).
builder.Services.AddHttpContextAccessor();

// The agent API (/api/v1) is the only rate-limited surface: fixed window per client IP
// (UseForwardedHeaders runs first, so RemoteIpAddress is the real client behind nginx).
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.OnRejected = (context, _) =>
    {
        context.HttpContext.Response.Headers.RetryAfter = "60";
        return ValueTask.CompletedTask;
    };
    options.AddPolicy("api", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 60,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0
        }));
    // The paid endpoint gets a much stricter per-minute layer (a per-IP daily quota and a
    // concurrency guard are enforced inside the endpoint itself).
    options.AddPolicy("ask", context =>
    {
        var ai = context.RequestServices.GetRequiredService<IOptionsMonitor<AiOptions>>().CurrentValue;
        return RateLimitPartition.GetSlidingWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new SlidingWindowRateLimiterOptions
            {
                PermitLimit = Math.Max(1, ai.PerIpPerMinute),
                Window = TimeSpan.FromMinutes(1),
                SegmentsPerWindow = 4,
                QueueLimit = 0
            });
    });
});

var app = builder.Build();

// Must run before anything that inspects the request scheme (HSTS, redirects).
app.UseForwardedHeaders();

app.UseRateLimiter();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

// Baseline security headers (X-Frame-Options/frame-ancestors already come from
// the framework) and a real cache lifetime for the self-hosted fonts: they are
// not fingerprinted, so MapStaticAssets serves them no-cache by default and
// every visit revalidates ~120 KB of woff2.
app.Use(async (context, next) =>
{
    var headers = context.Response.Headers;
    headers.XContentTypeOptions = "nosniff";
    headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
    if (context.Request.Path.StartsWithSegments("/css/fonts"))
    {
        context.Response.OnStarting(() =>
        {
            context.Response.Headers.CacheControl = "public, max-age=604800";
            return Task.CompletedTask;
        });
    }
    await next();
});

app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();

app.UseAntiforgery();

app.MapStaticAssets();

// Crawlers: sitemap built from the same in-memory catalogues the pages use.
// robots.txt (wwwroot) points here.
app.MapGet("/sitemap.xml", (ManifestService manifest, DocsService docs, HttpContext context) =>
{
    var baseUrl = $"{context.Request.Scheme}://{context.Request.Host}";
    var urls = new List<string> { "/", "/events", "/effects", "/docs", "/ask" };
    urls.AddRange(manifest.Manifest.Entities.Select(e => "/" + e.Id));
    urls.AddRange(manifest.CoreKinds.Select(k =>
        "/core/" + (ManifestService.KindSlug(k) is "property" ? "properties" : ManifestService.KindSlug(k) + "s")));
    urls.AddRange(docs.Sections.SelectMany(s => s.Pages.Concat(s.Index is { } i ? [i] : Array.Empty<DiSkyAtlas.Models.DocPage>()))
        .Select(p => "/docs/" + p.Slug));

    var sb = new StringBuilder();
    sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n");
    sb.Append("<urlset xmlns=\"http://www.sitemaps.org/schemas/sitemap/0.9\">\n");
    foreach (var url in urls.Distinct())
        sb.Append("  <url><loc>").Append(baseUrl).Append(System.Security.SecurityElement.Escape(url)).Append("</loc></url>\n");
    sb.Append("</urlset>\n");
    return Results.Text(sb.ToString(), "application/xml", Encoding.UTF8);
});

// Agents: the read-only markdown API (/api/v1) + /llms.txt.
app.MapAtlasApi();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
