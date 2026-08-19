using DiSkyAtlas.Models;
using DiSkyAtlas.Services.Docs;

namespace DiSkyAtlas.Components.Docs;

/// <summary>
/// Cascaded through the markdown component tree: the page being rendered, the construct
/// registry, the docs service and the page-scoped interactive state (toggles).
/// </summary>
public sealed record DocRenderContext(
    DocPage Page,
    DocComponentRegistry Registry,
    DocsService Docs,
    DocPageState State);
