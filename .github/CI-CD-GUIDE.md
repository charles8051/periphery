# CI/CD Quick Reference

This document provides quick commands and explanations for the Periphery CI/CD workflows.

---

## Workflows

### 1. Build and Test (`build.yml`)

**Triggers:**
- Manual dispatch via GitHub UI only (cross-platform runs are expensive in
  runner-minutes; the dispatch form picks which OSes to run)

**What it does:**
- Builds on any ticked subset of Windows, Linux, and macOS
- Tests on .NET 10.0 only. Published libraries also target `net8.0`, which is **compiled but never tested** — a deliberate, documented trade (ADR-0069, superseding ADR-0067)
- Runs unit tests (`--filter "Category!=Integration"`; integration tests need
  live hardware and are not run on hosted runners)
- Uploads `.trx` test results as an artifact (best-effort — never gates the job)

**Job matrix:** up to 3 build combinations (one per ticked OS)

---

### 2. Publish (`publish.yml`)

**Triggers:**
- Git tags matching `v*.*.*` (e.g., `v1.0.0`)

**What it does:**
- Builds in Release configuration
- Runs all tests
- Packs NuGet packages
- Publishes to nuget.org via Trusted Publishing (OIDC)

---

## Running Tests Locally

```bash
# All unit tests (fast, ~7 seconds)
dotnet test --filter "Category!=Integration"

# Integration tests only (slow, ~6+ minutes, Windows only)
dotnet test --filter "Category=Integration"

# All tests (unit + integration)
dotnet test

# With code coverage
dotnet test --collect:"XPlat Code Coverage"
```

---

## Creating a Release

### 1. Prepare the release

```bash
# Ensure all tests pass
dotnet test

# Commit all changes
git add .
git commit -m "chore: prepare release v1.0.0"
git push origin main
```

### 2. Tag and push

```bash
# Create annotated tag
git tag -a v1.0.0 -m "Release v1.0.0"

# Push tag (triggers publish workflow)
git push origin v1.0.0
```

### 3. Monitor workflow

- Go to https://github.com/charles8051/periphery/actions
- Wait for the "Release" workflow
- Check the packages at https://www.nuget.org/profiles/charles8051

---

## Debugging CI Failures

### Build failures

1. Check the specific job that failed (OS + framework combination)
2. Download test result artifacts from the workflow run
3. Reproduce locally:
   ```bash
   # Windows
   dotnet build
   dotnet test --filter "Category!=Integration"
   
   # Linux (WSL or VM)
   dotnet build
   dotnet test --filter "Category!=Integration"
   ```

### Test failures

1. Download `test-results-*.trx` artifact from failed job
2. View in Visual Studio or convert to HTML:
   ```bash
   # Install trx2html
   dotnet tool install -g trx2html
   
   # Convert
   trx2html test-results.trx
   ```

### Code coverage

- No workflow collects coverage today — `build.yml` builds and runs the unit
  sweep only. Every test project still carries `coverlet.collector`, so
  `dotnet test --collect:"XPlat Code Coverage"` works locally.

---

## Badge URLs

Add these to your README.md:

```markdown
![Build and Test](https://github.com/charles8051/periphery/actions/workflows/build.yml/badge.svg)
![Publish](https://github.com/charles8051/periphery/actions/workflows/publish.yml/badge.svg)
```

---

## Workflow Customization

### Skip CI on commit

```bash
git commit -m "docs: update README [skip ci]"
```

### Run integration tests in CI

By default, integration tests run but don't fail the build (`continue-on-error: true`).

To make them required:
1. Edit `.github/workflows/build.yml`
2. Remove `continue-on-error: true` from integration-tests job
3. Add timeout to prevent long runs:
   ```yaml
   - name: Run integration tests
     timeout-minutes: 15
     run: dotnet test --filter "Category=Integration"
   ```

### Add code quality checks

```yaml
# Add to build.yml after restore step
- name: Format check
  run: dotnet format --verify-no-changes

- name: Analyze
  run: dotnet build /p:AnalysisMode=All
```

---

## Environment Variables

CI workflows set these environment variables:

```yaml
DOTNET_SKIP_FIRST_TIME_EXPERIENCE: true  # Skip first-run prompts
DOTNET_CLI_TELEMETRY_OPTOUT: true        # Disable telemetry
```

---

## Secrets

Required secrets (configure in repo settings):

| Secret | Purpose | Required? |
|--------|---------|-----------|
| `NUGET_USER` (variable) | nuget.org profile name, for the OIDC exchange | ✅ Required to publish |
| `CODECOV_TOKEN` | Upload coverage to Codecov | ❌ Optional |

---

## Performance Tips

1. **Restore cache** (optional, adds complexity):
   ```yaml
   - uses: actions/cache@v3
     with:
       path: ~/.nuget/packages
       key: ${{ runner.os }}-nuget-${{ hashFiles('**/*.csproj') }}
   ```

2. **Parallel test execution** (already enabled by default in dotnet test)

3. **Reduce test verbosity** for faster CI:
   ```yaml
   run: dotnet test --verbosity minimal
   ```

---

## Troubleshooting

### "Package already exists" error during publish

- nuget.org does not allow deleting or replacing a published version
- `--skip-duplicate` is already set, so a re-run of the same tag is a no-op
- Bump the version and tag again

### Integration tests timeout

- Increase `timeout-minutes` in workflow
- Or run fewer iterations in stress tests for CI

### macOS/Linux tests fail but Windows passes

- Likely platform-specific code issue
- Check for `OperatingSystem.IsWindows()` guards
- Review WMI vs Linux/macOS provider differences

---

For more details, see `docs/ARCHITECTURE.md` section 10.4.
