---
paths:
  - "**/*.cs"
  - "**/*.csproj"
  - "Directory.*.props"
  - "Directory.Build.targets"
---

# C# rules

The build runs with `TreatWarningsAsErrors` and the analyzers from `Directory.Build.props` / `.editorconfig`.
A change is not finished until `dotnet build ShopFlow.slnx` reports 0 warnings and
`dotnet format whitespace ShopFlow.slnx --verify-no-changes` passes.

## Style

- File-scoped namespaces matching the folder; `using` directives outside the namespace, `System` first.
- Primary constructors for services; `required init` properties for DTOs and contracts.
- `sealed` by default (classes, records). Infrastructure adapters of application ports are `internal sealed`.
- Private fields `_camelCase`, constants and static readonly fields `PascalCase`.
- Collection expressions (`[]`) and pattern matching where they make the code shorter, not cleverer.

## Correctness in a distributed system

- Pass `CancellationToken` through every async call (CA2016 is a warning, CS4014 an error).
- `ConfigureAwait(false)` in libraries (BuildingBlocks, Application, Infrastructure). Hosts may omit it.
- Time comes from `TimeProvider` (injected), never `DateTime.Now/UtcNow` or `DateTimeOffset.UtcNow`.
- New identifiers that are sorted or used as keys: `Guid.CreateVersion7()` (time-ordered), see `UuidV7`.
- Money is `Money` (SharedKernel), amounts in minor units at the boundaries; never `double` for money.
- Expected business failures return `Result<T>` with an error code; exceptions are for the unexpected and for
  infrastructure failures that the messaging layer classifies (transient → retry, permanent → DLQ).
- Logging: `LoggerMessage` source-generated methods with structured properties; no string interpolation in log calls,
  no secrets or personal data (e-mail, address, payment token) in logs or span attributes.
- Options classes are validated on start (`ValidateDataAnnotations().ValidateOnStart()`); configuration keys are
  addresses and client IDs, never keys or connection strings with secrets.

## Dependencies

- Versions only in `Directory.Packages.props` (Central Package Management). A `.csproj` has `PackageReference`
  without `Version`. Explain a new package with a `<!-- WHY: -->` comment (licence, alternatives considered).
- Respect the layers (see `tests/ShopFlow.Architecture.Tests/LayeringTests.cs`): Domain → SharedKernel only;
  Application → ports, no Azure SDK, EF Core or Cosmos types; one context never references another context's assemblies.

## Comments

- Comments explain *why* and *what Azure does*, not what the next line does. Use the established prefixes:
  `// AZURE:` (service behavior, limits, billing) and `// WHY:` (design decision and the alternative rejected).
- Public types and non-obvious members get `/// <summary>`; contract and port members always do.
- English only. Customer-facing Polish strings exist only in the files listed in `scripts/english-allowlist.json`.
- EF Core migrations (`**/Migrations/*.cs`) are generated code: do not edit them by hand, use the `/ef-migration` skill.
