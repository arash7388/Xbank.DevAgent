# Source publication review

Reviewed the C# application, configuration, examples, tests, documentation, tracked generated
output, and XML metadata inside the presentation and bundled NuGet packages.

## Changes

- Removed personal checkout paths and Jira credentials from application defaults.
- Replaced internal GitLab/Jira connection defaults with public GitLab and an unset Jira URL.
  The repository list starts empty; developers configure their own repositories in Settings.
- Removed the personal GitHub repository default and internal Cognia endpoint from the UI.
- Made the AIFA base URL configurable with `AIFA_ENDPOINT` for both API and CLI; the built-in
  URL is an example placeholder. Browser workflows obtain the configured endpoint from the API.
- Made Jira requester custom field IDs configurable in Settings (`Jira:RequesterField`).
  Issue creation no longer assumes a particular organization's Jira project or custom field.
- Branch generation accepts any Jira project key. Bare task numbers use `TASK` as their prefix.
- Replaced organization hostnames and personal repository names in test fixtures with examples.
- Removed 52 generated files from Git tracking under `CSharp/.artifacts/`, including copied
  configuration containing a plaintext Jira password and generated files containing personal paths.
  Local copies remain on disk and must not be included in a manually created source archive.
- Added Git and Docker exclusions for generated artifacts, local configuration, environment files,
  encrypted profiles and private key files. Existing databases and build outputs remain excluded.

Public provider URLs, loopback service addresses, protocol constants, example paths, synthetic
test credentials, package metadata and license attribution are intentionally retained.

## Before publishing

The old Jira password exists in Git history, including a tracked generated configuration copy
in commit `be35e0e`. Rotate that credential. Removing the current files does not erase old commits,
tags, branches, existing clones, or copies in local build directories.

Publish a fresh snapshot containing only the sanitized C# source, without `.git` or ignored files,
or separately sanitize all relevant history before pushing an existing repository. Commit/review
all source changes first: several cleanup changes are in the working tree while generated-file
removals are staged. The old `HEAD` is not a sanitized publication snapshot. No history rewrite,
commit, remote push, or credential rotation was performed by this review.

The presentation PDF and PowerPoint still contain the author's name in document metadata.
This attribution is not a credential; review whether to include those documents in your release.
The XML metadata scan does not certify compiled binaries, compressed PDF content, or every pixel
in screenshots. Review bundled SDK redistribution permissions separately.

This review covers `CSharp/`. It is not a certification of a whole-repository publication or its
complete Git history, and pattern-based credential searches cannot prove the absence of all secrets.

## Validation

A working-snapshot scan across 270 C# source/documentation files found no copies of the known
password, organization hostnames, or personal checkout paths. Targeted C# test runs passed
(196 tests, followed by 77 tests covering the added cases and automatic completion), and all
29 UI test scripts passed. Build/test output had no compiler errors. A standalone syntax check
of the entire `app.js` could not complete with the installed older Node runtime because existing
code uses the newer `??=` operator; the UI scripts execute the relevant functions separately.
