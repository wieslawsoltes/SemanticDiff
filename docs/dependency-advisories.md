# Dependency Advisories

## Tmds.DBus.Protocol 0.21.2

`dotnet build` currently reports `NU1903` for `Tmds.DBus.Protocol` 0.21.2, a transitive dependency pulled through the Uno desktop stack. SemanticDiff does not reference this package directly.

Current handling:

- Keep the warning visible in normal builds.
- Track the advisory until the Uno package graph moves to a fixed transitive version.
- Re-check after each Uno.Sdk update.
- Do not suppress `NU1903` globally; it should remain visible in CI and local builds.