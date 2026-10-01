## What does this change?

<!-- A short description of the change and why it is needed. Link the related issue. -->

Closes #

## How was it tested?

<!-- Tests added or updated, manual verification steps, Windows versions used for agent changes. -->

## Checklist

- [ ] All commits are signed off (`git commit -s`, DCO).
- [ ] Commit messages follow Conventional Commits.
- [ ] Tests cover the change (authorization, path handling, process lifecycle and agent protocol changes always need tests).
- [ ] `dotnet build` has no warnings and `dotnet format --verify-no-changes` passes.
- [ ] Frontend lint, typecheck and tests pass (if the frontend changed).
- [ ] Documentation and ADRs are updated if behavior or architecture changed.
- [ ] No secrets, credentials or personal data are logged or committed.
