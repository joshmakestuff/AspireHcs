# Contributing

AspireHcs is experimental and pre-alpha. There is no support commitment and no
compatibility guarantee between releases; the builder API changes when the design needs it.

## Issues

Bugs, questions and proposals go in
[issues](https://github.com/joshmakestuff/AspireHcs/issues). Include the AspireHcs version,
the pinned hcsctl version (`eng/Get-HcsCtl.ps1` prints it), the Windows build, whether the
process was elevated, and the dashboard or test output when it applies.

## Changes

- Open an issue first for anything beyond a small fix, so the scope is agreed before the work.
- One change per pull request. `dotnet build && dotnet test` must pass; `ci.yml` runs the
  same on `windows-latest` and verifies the package claims.
- Comments state what the code does and the HCS or Aspire fact it depends on, not how the
  decision was reached. Measured behaviour goes in the issue, not the code.
- All HCS access goes through hcsctl's `--json` contract. Do not add HCS interop to this
  package; if hcsctl lacks a verb, it grows there first.
- Windows-only. Integration tests need Hyper-V and prepared images; they skip without the
  environment variables the [README](README.md) names.
