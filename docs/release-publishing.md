# Release publishing

NuGet publication uses [Trusted Publishing](https://learn.microsoft.com/en-us/nuget/nuget-org/trusted-publishing). GitHub issues an OIDC token and `NuGet/login@v1` exchanges it for a short-lived publishing credential. No long-lived `NUGET_API_KEY` Actions secret is required. npm authentication is unchanged.

## One-time NuGet account setup

Create a Trusted Publishing policy in the NuGet account that owns the Elwood packages:

| Field | Value |
| --- | --- |
| Policy name | `elwood-github-release` (descriptive name) |
| Repository owner | `max-favilli` |
| Repository | `elwood` |
| Workflow file | `release.yml` — filename only |
| Environment | Leave empty; the job does not use a GitHub Environment |
| Package pattern | `Elwood.*` |
| Publishing scopes | New packages and new versions of existing packages |

Set repository Actions **variable** `NUGET_USER` to the NuGet profile username associated with the policy, not an email address or GitHub username unless those names happen to match. This username is not a credential. Do not paste tokens into issues, chat, logs or repository files. After a successful OIDC publication, the obsolete stored API key can be revoked/deleted.

Only the NuGet authentication/publishing jobs receive `id-token: write`; only the GitHub Release job receives `contents: write`. Authentication is checked before expensive builds/tests, and that temporary credential is discarded. The publishing job requests a fresh one-hour credential after packing and all verification gates, and exposes it only to the push steps. Core publication and public availability are checked before dependent packages are pushed.

To check the policy independently without building or publishing:

```powershell
gh workflow run release.yml --repo max-favilli/elwood --ref main -f tag=v0.8.0-rc.1 -f verify-nuget-login-only=true
```

This confirms NuGet accepts the workflow identity and issues a temporary credential. Actual package publishing permissions and public availability are verified by the complete release, not by this authentication check. Authentication-only runs use a separate concurrency group and do not cancel or hold up an existing release.

## Normal release and recovery

Pushing a `v*` version tag triggers `.github/workflows/release.yml`. A manual run from `main` can publish an **existing** tag using the latest workflow, for example:

```powershell
gh workflow run release.yml --repo max-favilli/elwood --ref main -f tag=v0.8.0-rc.1
```

The workflow resolves `refs/tags/<tag>` once, then uses that exact commit for every test, native build, runtime bundle and package. It repeats all release verification gates before publishing; this is not a bypass for failed tests. The source tag is not moved. Bundles record the tag's source commit, even when the workflow itself runs from a newer `main` commit. Manual runs from other branches are skipped. Rebuilt ZIP bytes may differ from earlier attempts, so verify the checksums and provenance of the **new run's** artifacts before recording publication evidence.

Use this manual path when fixing the workflow itself: GitHub's ordinary rerun uses the original workflow revision, including its old authentication configuration. Unchanged npm/NuGet versions are skipped. A tag alone, successful build, or downloadable Actions artifact does not prove publication. Confirm the public candidate packages and GitHub Release assets before closing the handoff checklist.
