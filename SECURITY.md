# Security Policy

Pulsar controls processes and files on remote machines, so we take security reports seriously.

## Supported versions

Pulsar is pre-alpha. Until the first release, only the `main` branch receives fixes.

## Reporting a vulnerability

**Do not open a public issue for security problems.**

Report vulnerabilities privately through GitHub's [private vulnerability reporting](https://docs.github.com/en/code-security/security-advisories/guidance-on-reporting-and-writing-information-about-vulnerabilities/privately-reporting-a-security-vulnerability) ("Report a vulnerability" in the repository's **Security** tab).

Please include:

- affected component (panel, agent, frontend, manifest) and version or commit;
- steps to reproduce or a proof of concept;
- the impact you expect (for example remote code execution, privilege escalation, path traversal, data exposure).

We aim to acknowledge reports within 5 business days and will keep you informed while we work on a fix. We will credit reporters in the advisory unless you prefer to stay anonymous.

## Scope

Especially relevant areas:

- panel ↔ agent authentication, command signing and replay protection;
- authorization (RBAC) bypasses and access to other users' servers;
- path traversal, junction/symlink escapes and zip-slip in the file manager and backups;
- argument or console command injection through server variables or manifests;
- isolation between game servers on the same node.

Vulnerabilities in third-party game servers or plugins themselves are out of scope, but escapes from Pulsar's isolation of them are in scope.
