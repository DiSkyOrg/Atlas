using BlazorBlueprint.Components;
using DiSkyAtlas.Components;
using DiSkyAtlas.Components.Docs;
using DiSkyAtlas.Services;
using DiSkyAtlas.Services.Docs;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

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

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();

app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
