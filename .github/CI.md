# Continuous Integration

This repo has a GitHub Actions workflow at `.github/workflows/build-and-test.yml`.

## What it does
On every push / pull request to `main` or `master` (and on manual trigger from the
Actions tab), it runs on a **Windows** runner and:

1. Installs the stable .NET 8 SDK (not your local preview SDK)
2. Restores both projects
3. Builds the app in Release — **this catches the XAML / C# compile breaks that have
   previously only surfaced on the local build machine**
4. Runs the unit test suite (RemuxCommit, MediaHealth, FingerprintCache, FileCopy
   routing, orphan sweep, FilenameParser, FileRenameService)

## Why it matters
Every build break this project hit was caught only when you compiled locally and pasted
the error back. With CI, a broken commit is flagged automatically — usually within a few
minutes — before you ever pull it to build. The same goes for a change that accidentally
breaks a test on the file-destroying paths (remux Replace/Restore, duplicate detection).

## How to use it
- It runs automatically once this folder is pushed to a GitHub repo.
- Green check = the app compiles and all tests pass on a clean Windows environment.
- Red X = click into the run to see exactly which step failed and why.

## Requirements
- The code must be in a GitHub repository (the workflow lives under `.github/workflows/`).
- No secrets or credentials are needed — all NuGet packages are public.

## Optional: status badge
Add this to the top of a project README (replace OWNER/REPO):

```
![Build and Test](https://github.com/OWNER/REPO/actions/workflows/build-and-test.yml/badge.svg)
```
