---
title: "SemanticDiff"
layout: simple
og_type: website
---

<div class="semanticdiff-hero">
  <div>
    <div class="semanticdiff-eyebrow"><i class="bi bi-diagram-3" aria-hidden="true"></i> Graph-first code review</div>
    <h1>SemanticDiff</h1>
    <p class="lead"><strong>SemanticDiff</strong> turns Git changes into interactive file, symbol, review, history, and blame graphs. It is a desktop workbench for reviewing code when a flat patch does not show the shape of the change.</p>
    <div class="semanticdiff-hero-actions">
      <a class="btn btn-primary btn-lg" href="articles/getting-started/overview"><i class="bi bi-signpost-split" aria-hidden="true"></i> Start with the app</a>
      <a class="btn btn-outline-secondary btn-lg" href="articles/packages"><i class="bi bi-box-seam" aria-hidden="true"></i> Choose a package</a>
      <a class="btn btn-outline-secondary btn-lg" href="api"><i class="bi bi-braces" aria-hidden="true"></i> Browse API</a>
    </div>
    <div class="semanticdiff-pill-row">
      <span class="semanticdiff-pill">GitHub and GitLab review workflows</span>
      <span class="semanticdiff-pill">Semantic graph navigation</span>
      <span class="semanticdiff-pill">History and blame timelines</span>
      <span class="semanticdiff-pill">TextMate file rendering</span>
      <span class="semanticdiff-pill">Skia export</span>
      <span class="semanticdiff-pill">Reusable libraries</span>
    </div>
  </div>
  <div class="semanticdiff-graph-card" aria-label="SemanticDiff graph illustration">
    <svg viewBox="0 0 560 360" role="img" aria-label="Graph showing files, symbols, review comments, and history lanes">
      <defs>
        <linearGradient id="node" x1="0" y1="0" x2="1" y2="1"><stop stop-color="#ffffff"/><stop offset="1" stop-color="#dff5ff"/></linearGradient>
      </defs>
      <rect width="560" height="360" fill="transparent"/>
      <g stroke="#9be7ff" stroke-width="4" fill="none" opacity="0.75">
        <path d="M86 248 C160 132 250 276 322 128 S434 98 492 180"/>
        <path d="M86 110 C180 220 226 96 330 214 S452 280 500 102"/>
        <path d="M150 304 C190 250 218 238 270 230 S366 215 412 260"/>
      </g>
      <g font-family="Segoe UI, Arial, sans-serif" font-size="15" font-weight="700">
        <g transform="translate(55 78)"><rect width="145" height="72" rx="14" fill="url(#node)" stroke="#58c7ff"/><text x="18" y="32" fill="#07182b">File node</text><text x="18" y="54" fill="#2c6074" font-size="12">diff + annotations</text></g>
        <g transform="translate(300 92)"><rect width="160" height="72" rx="14" fill="url(#node)" stroke="#16a34a"/><text x="18" y="32" fill="#07182b">Symbol map</text><text x="18" y="54" fill="#2c6074" font-size="12">semantic edges</text></g>
        <g transform="translate(105 230)"><rect width="170" height="72" rx="14" fill="url(#node)" stroke="#f59e0b"/><text x="18" y="32" fill="#07182b">Review thread</text><text x="18" y="54" fill="#2c6074" font-size="12">click to code</text></g>
        <g transform="translate(365 238)"><rect width="145" height="72" rx="14" fill="url(#node)" stroke="#ef3f8a"/><text x="18" y="32" fill="#07182b">Blame</text><text x="18" y="54" fill="#2c6074" font-size="12">commit history</text></g>
      </g>
      <g fill="#58c7ff"><circle cx="86" cy="110" r="7"/><circle cx="322" cy="128" r="7"/><circle cx="492" cy="180" r="7"/></g>
    </svg>
  </div>
</div>

## Documentation Sections

<div class="semanticdiff-link-grid">
  <a class="semanticdiff-link-card" href="articles/getting-started">
    <span class="semanticdiff-link-card-title"><i class="bi bi-signpost" aria-hidden="true"></i> Getting Started</span>
    <p>Install prerequisites, run the app, open a repository, and load your first branch, range, PR, or MR.</p>
  </a>
  <a class="semanticdiff-link-card" href="articles/packages">
    <span class="semanticdiff-link-card-title"><i class="bi bi-boxes" aria-hidden="true"></i> Packages</span>
    <p>Choose the reusable package layer for models, diff parsing, Git workflows, layout, rendering, semantics, or Uno controls.</p>
  </a>
  <a class="semanticdiff-link-card" href="articles/concepts">
    <span class="semanticdiff-link-card-title"><i class="bi bi-lightbulb" aria-hidden="true"></i> Concepts</span>
    <p>Understand the graph model, workspace tabs, semantic analysis, review integration, and Skia rendering pipeline.</p>
  </a>
  <a class="semanticdiff-link-card" href="articles/guides">
    <span class="semanticdiff-link-card-title"><i class="bi bi-journal-code" aria-hidden="true"></i> Guides</span>
    <p>Follow focused workflows for reviews, Git history, blame, file tabs, symbol maps, graph export, and publishing.</p>
  </a>
  <a class="semanticdiff-link-card" href="articles/reference">
    <span class="semanticdiff-link-card-title"><i class="bi bi-collection" aria-hidden="true"></i> Reference</span>
    <p>Review CI, docs publishing, dependency notes, package API coverage, and extraction planning.</p>
  </a>
  <a class="semanticdiff-link-card" href="api">
    <span class="semanticdiff-link-card-title"><i class="bi bi-braces-asterisk" aria-hidden="true"></i> API Reference</span>
    <p>Browse generated API documentation for the reusable SemanticDiff libraries and Uno controls.</p>
  </a>
</div>

## Capability Snapshot

<div class="semanticdiff-metric-row">
  <div class="semanticdiff-metric"><strong>10</strong> reusable package surfaces</div>
  <div class="semanticdiff-metric"><strong>5</strong> left-pane workflows: Files, Git, Review, Symbols, Settings</div>
  <div class="semanticdiff-metric"><strong>4</strong> main tab families: graph, file, history, blame</div>
  <div class="semanticdiff-metric"><strong>3</strong> export targets: SVG, PNG, PDF</div>
</div>

## Start Here

- Use [Getting Started Overview](articles/getting-started/overview) to understand the application workflow.
- Use [Package Overview](articles/packages) when embedding SemanticDiff libraries in another application.
- Use [Git Workflows](articles/guides/git-workflows) for branches, ranges, pull requests, and merge requests.
- Use [Review Comments](articles/guides/review-comments) for GitHub/GitLab review threads and code annotations.
- Use [API Coverage](articles/reference/package-api-coverage) to see which projects are included in generated API docs.
