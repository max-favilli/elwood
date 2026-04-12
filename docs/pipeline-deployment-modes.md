# Pipeline Deployment Modes

How pipelines move from editing to live execution. Elwood supports two modes — organizations choose based on their need for review and governance.

---

## Mode 1: Save = Live (no review)

The simplest model. When you save a pipeline in the portal, it's immediately live.

```
You edit in the portal
    ↓ click Save
API writes to the pipeline store (Blob Storage in production)
    ↓ simultaneously
API updates the pipeline cache (Redis — route table + content)
    ↓
Pipeline is LIVE immediately — the next HTTP request uses the new version
    ↓ optionally
API commits to git (for history and audit trail, not as a gate)
```

**Git is optional and passive.** If configured, every save creates a git commit on the main branch — you get a full history of who changed what and when. But git is not in the critical path: the pipeline goes live from blob storage + Redis, not from git.

**Good for:** solo developers, dev/test environments, rapid iteration, trying out Elwood for the first time.

**Risk:** no review gate. A bad save goes live immediately. Mitigated by: the Validate button in the portal, execution testing via the Run panel, and git history for rollback.

---

## Mode 2: Save = Draft, Merge = Live (with review)

The enterprise model. Saving in the portal creates a draft. The pipeline goes live only after a pull request is reviewed and merged.

```
You edit in the portal
    ↓ click Save
API saves your changes to a BRANCH (not main)
Your changes are stored but NOT live
    ↓ click "Submit for review"
API pushes the branch to the git remote and opens a pull request
    ↓
Your colleague reviews the PR in GitHub / Azure DevOps / GitLab
They see the YAML diff and script changes
    ↓ approve and merge to main
    ↓
Merge triggers a webhook → API receives it
    ↓
API reads the merged changes from main
API updates Blob Storage + Redis
    ↓
Pipeline is LIVE now
```

**What's live (in Redis) always matches the main branch.** What you see in the portal while editing is your branch — it may be ahead of what's live.

**Good for:** teams, production environments, regulated industries, any organization that requires change review before deployment.

### What the portal needs for Mode 2

- **Branch awareness** — the portal shows which branch you're editing and whether it's ahead of main
- **PR creation** — "Submit for review" button creates a PR in the configured git provider
- **Draft indicator** — pipelines with unpublished changes show a "draft" badge
- **Merge webhook handler** — API endpoint that receives the merge event and updates blob + Redis
- **Conflict resolution** — if two people edit the same pipeline on different branches, the PR merge in the git provider handles conflicts (standard git merge)

### Developer workflow (VS Code alternative)

Developers who prefer their editor can skip the portal entirely:

```bash
git clone https://github.com/my-org/elwood-pipelines.git
cd elwood-pipelines

# Edit pipelines in VS Code (YAML + .elwood files)
vim crm-newsletter/pipeline.elwood.yaml

# Standard git workflow
git checkout -b fix/crm-newsletter-timeout
git commit -am "increase CRM connection timeout to 120s"
git push origin fix/crm-newsletter-timeout

# Open PR in GitHub/DevOps → review → merge → live
```

The portal and VS Code workflows produce the same result — a git commit on main that triggers a deploy.

---

## How the pieces connect

```
                    ┌─────────────────────┐
                    │   Portal (browser)   │
                    └──────────┬──────────┘
                               │ save
                    ┌──────────▼──────────┐
                    │  Runtime API         │
                    │  (serverless)        │
                    └──┬───────┬───────┬──┘
                       │       │       │
              ┌────────▼──┐ ┌──▼────┐ ┌▼────────┐
              │ Blob       │ │ Redis │ │ Git      │
              │ Storage    │ │       │ │ (opt.)   │
              │            │ │ route │ │          │
              │ YAML +     │ │ table │ │ history  │
              │ scripts    │ │   +   │ │ PRs      │
              │ (source    │ │ content│ │ audit    │
              │  of truth) │ │ cache │ │ trail    │
              └────────────┘ └───┬───┘ └──────────┘
                                 │
                    ┌────────────▼────────────┐
                    │  Function App (executor) │
                    │  stateless — reads Redis │
                    └─────────────────────────┘
```

**Mode 1:** API writes directly to Blob + Redis. Git commit is a side effect.

**Mode 2:** API writes to a git branch. Only when the branch merges to main does the API update Blob + Redis.

---

## Choosing a mode

| Question | Mode 1 | Mode 2 |
|---|---|---|
| How many people edit pipelines? | 1–2 | Team |
| Is this a production environment? | No (dev/test) | Yes |
| Do you need change review? | No | Yes |
| Do you need audit trail? | Nice to have | Required |
| How fast should changes go live? | Instantly | After review (minutes/hours) |

An organization can use **Mode 1 for dev** and **Mode 2 for production** — same Elwood instance, different configuration per environment.

---

## Current status

| Feature | Status |
|---|---|
| Mode 1 (save = live, blob + Redis) | Implementing now |
| Git commit on save (Mode 1 audit trail) | Planned |
| Mode 2 (branch + PR + merge webhook) | Planned — Phase 3b refinement |
| VS Code + git push workflow | Works today (manual deploy) |
