## Summary

<!-- What does this PR change, and why? Link the roadmap/plan section or issue if relevant. -->

## Pod / Area

<!-- e.g. Pod 1 Engine, Pod 2A Swarm, Pod 2B UI, Pod 3 Terminal, Core, Docs -->

## Checklist

- [ ] Branched off latest `main`; this is a single feature/fix (not a mixed grab-bag).
- [ ] `dotnet build` passes.
- [ ] `dotnet test` passes (added/updated tests for Core changes).
- [ ] `dotnet format` run — no style diffs.
- [ ] EF entities changed? Migration + snapshot committed together.
- [ ] No secrets, tokens, `.env`, or `*.db` files committed.
- [ ] LibGit2Sharp access goes through `IGitService.ExecuteWithRepo` (no leaked native handles).
- [ ] Docs updated if behavior changed (README) or the plan changed (roadmap/plan).

## Notes for reviewers

<!-- Anything tricky, follow-ups, or areas you want extra eyes on. -->
