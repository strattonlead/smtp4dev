# Fork changes

This is a fork of [rnwood/smtp4dev](https://github.com/rnwood/smtp4dev), maintained by
Stratton Lead as the mail engine behind Deadletter, an agent-driven mail testing sandbox.

The patches below are **not contributed upstream**. They exist to make one smtp4dev process
serve several tenants at once over the public internet, which is not what upstream is for. No
CLA applies: that agreement is triggered by opening a pull request against `rnwood/smtp4dev`,
never by forking, patching, building or operating the software. BSD governs all of that and
asks only that attribution is preserved, which it is.

This document is the authoritative specification of the diff. Keep it current: when a patch
changes, change the section here in the same commit.

## Patch inventory

| # | Patch | Status | Files |
|---|---|---|---|
| FP1 | Selective listener restart | Landed `5d52cdfe` | `Smtp4devServer.cs`, `ImapServer.cs`, `ServerOptions.cs`, 3 test files |
| FP2 | CI: build, test and publish binaries | Landed `be7b50fe` | `.github/workflows/build.yml` |

## Versioning

Release tags are `v<upstream-version>-p<patchlevel>`. The upstream version identifies the
smtp4dev release the patch branch sits on; the patch level increments whenever our diff changes
against that same upstream version. Current: `v3.3.0-p1`.

Consumers pin an exact tag. There is no `latest` and no `main` tag, because a moving engine tag
makes a Deadletter deployment irreproducible.

## FP1 - Selective listener restart

**Status:** landed as `5d52cdfe`.

### Why

Every settings write used to restart the SMTP and IMAP listeners. Both servers compared the
incoming `ServerOptions` against a full snapshot taken at start, so editing a validation
expression, adding a mailbox or adding a user tore down every in-flight connection and clients
saw a connection reset rather than a protocol-level error.

Deadletter writes settings constantly - that is how fault rules and mailbox leases are applied -
so under the original behaviour no fault rule could be installed without killing the sessions it
was meant to affect.

### What changed

Each server compares only the options it actually reads while creating its listener, expressed
as a `readonly record struct` so the field set is value-comparable and self-documenting:

- `Smtp4devServer.SmtpListenerConfig` covers the options read by `CreateSmtpServer` plus the four
  certificate options `CertificateHelper.GetTlsCertificate` resolves, so rotating a certificate
  still takes effect.
- `ImapServer.ImapListenerConfig` covers the options used to build the IMAP bindings.

`Smtp4devServer.ApplyConfigurationChanges` was added and is queued on the task queue when the
mailbox or retention options change, so a mailbox added at runtime is reconciled into the
database without a restart.

### Rebase notes

`ListenerConfigTests` asserts by reflection that each projection covers exactly the intended set
of `ServerOptions` members, and fails on any member it does not know about. **When upstream adds
a `ServerOptions` member, that test fails and forces the decision**: is the new option read while
the listener is created, or at the point of use? Answer it there rather than suppressing the test
- an option that is listener-relevant but missing from a projection silently never takes effect.

## FP2 - CI: build, test and publish binaries

**Status:** landed as `be7b50fe`.

### Why

Upstream's Azure Pipelines definition does not run for a fork. Without CI there is no regression
net, and under the no-upstream-contribution rule the fork's own tests are the only one there is.

### What changed

`.github/workflows/build.yml` runs on pushes to `master`, on pull requests, and on `v*` tags:

- **test** - restores, builds and runs `Rnwood.Smtp4dev.Tests`.
- **publish** - self-contained single-file publishes for `linux-x64`, `linux-musl-x64`,
  `linux-arm`, `linux-arm64`, `win-x64`, `win-arm64`. All cross-compile from the x64 runner, so
  the arm targets need no emulation.
- **release** - on `v*` tags only, creates a GitHub release with those archives attached.

### Known gaps

Two test sets are excluded from the CI run:

- `E2E.WebUI` needs Playwright browsers installed.
- `MemoryLeakStress` is a long-running stress test.

Under the no-upstream-contribution rule these are holes in the only regression net there is. See
FP6 for the nightly workflow that closes them.

The IMAP listener binds `IPAddress.IPv6Any` with no `AddressFamilyNotSupported` fallback, and
GitHub-hosted runners have no IPv6 stack, so the test job sets `SMTP4DEV_E2E_ARGS: --disableipv6`.
Any host without an IPv6 stack needs the same flag.
