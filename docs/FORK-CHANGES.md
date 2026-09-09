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
| FP3 | Multi-listener SMTP and IMAP TLS | Landed | `ServerOptions.cs`, `SmtpListenerOptions.cs`, `ImapListenerOptions.cs`, `ServerOptionsSource.cs`, `CertificateHelper.cs`, `Smtp4devServer.cs`, `ImapServer.cs` + 3 test files |
| FP4 | Cancellable `delay()` | Landed | `ScriptingHost.cs`, `IConnection.cs`, `Connection.cs`, `IConnectionChannel.cs`, `TcpClientConnectionChannel.cs`, `TestMocks.cs` + 1 test file |
| FP5 | GHCR container image | Landed | `.github/workflows/build.yml`, `Dockerfile.linux` |
| FP6 | Nightly workflow for excluded tests | Landed | `.github/workflows/nightly.yml` |

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

## FP3 - Multi-listener SMTP and IMAP TLS

**Status:** landed with this change.

### Why

`ServerOptions` had one `Port` and one `TlsMode`, so one process could serve exactly one SMTP
port under one TLS mode. Deadletter needs submission on 587 with STARTTLS, 465 with implicit TLS
and 2525 with STARTTLS at the same time.

IMAP had no TLS at all. Every `IPBindInfo` used the four argument constructor, which leaves
`SslMode` at `None` and the certificate null, and `IMAP_Session` refuses `STARTTLS` when the
certificate is null. So 993 was unavailable and 143 was plaintext only - carrying credentials and
staging password reset links over the public internet.

Three alternatives were rejected. Several engine instances over one SQLite store reintroduce the
concurrency problem FP1 exists to remove. A TLS terminating forwarder in front of 465 and 993
destroys the real client IP, which the sandbox records per session. Dropping to the ports the
engine supported leaves IMAP in plaintext, which is the thing that must not happen.

### What changed

`ServerOptions` gains `SmtpListeners` and `ImapListeners`, each entry a port and a TLS mode.
Neither is required: `ResolveSmtpListeners()` falls back to `Port`/`TlsMode` and
`ResolveImapListeners()` falls back to `ImapPort`, so a configuration that sets neither behaves
exactly as it did before. `RequiresTlsCertificate()` replaces the scalar TLS-mode check in
`CertificateHelper.GetTlsCertificate`, so a listener entry asking for TLS cannot end up with a
null certificate.

`Smtp4devServer` runs one `Rnwood.SmtpServer.SmtpServer` per resolved SMTP listener and
reconciles that set against the configuration, so adding or removing a listener starts and stops
only what changed. `SmtpListenerConfig` narrows to the options every listener shares; port and TLS
mode left it because they identify an individual listener.

`ImapServer` builds each `IPBindInfo` with the SSL carrying constructor, mapping
`ImplicitTls -> SslMode.SSL` and `StartTls -> SslMode.TLS`. `ImapListenerConfig` gains the
certificate options and the listener set. IMAP restarts wholesale on any listener change, because
LumiSoft's `IMAP_Server` owns its bindings array and has no per binding lifecycle.

A listener which asks for TLS with no resolvable certificate now throws rather than binding in
plaintext.

### Known limitation

`SecureConnectionRequired` remains a single server wide option rather than a per listener one. It
is enforced in `Smtp4devServer.OnMessageStart`, whose `MessageStartEventArgs` carries only the
session and the sender, so there is no session to listener attribution to key it on. Adding one
means widening the vendored `Rnwood.SmtpServer` API for a setting Deadletter applies uniformly
across all its SMTP ports.

### Rebase notes

**Both FP1 and FP3 project `ServerOptions` fields. Review both projections whenever
`ServerOptions` gains a member.** `ListenerConfigTests.EveryServerOption_IsClassified` fails until
a new member is classified as shared, listener set, or read at the point of use, which is the
check that stops a listener relevant option from silently never taking effect.

## FP4 - Cancellable `delay()`

**Status:** landed with this change.

### Why

`delay(n)` was a single `Thread.Sleep`, and `delay(-1)` was `Thread.Sleep(TimeSpan.MaxValue)`.
A delayed command therefore held an OS thread for its full duration whether or not the client was
still connected, and an indefinite delay held one until the process exited.

Validation expressions are a single global value per hook, so a `timeout` rule aimed at one
client is evaluated on every client's commands. The parked threads are shared by all of them,
which makes it a cross tenant denial of service rather than one client's problem. Expression
latency metrics cannot detect it, because the block *is* the latency they measure.

### What changed

`delay` waits in short increments and returns as soon as the connection goes away. `IConnection`
gains `IsConnected`, backed by a new `IConnectionChannel.IsPeerDisconnected` which probes the
socket.

It polls rather than waiting on `ConnectionClosedEventHandler` because `Connection.ProcessAsync`
is a single loop: while an expression runs nothing is reading the socket, so a client initiated
disconnect raises no event at all. A socket level probe sees that as well as a server initiated
close.

### Rebase notes

**This is the patch whose regression is silent.** A rebase which reverts `delay` to `Thread.Sleep`
breaks nothing visible - every test that does not specifically wait on a disconnect still passes,
and the failure only shows up as thread exhaustion under load. `ScriptingHostDelayTests` is the
only thing that catches it. Confirm it survived, and that it still asserts the early return rather
than only the elapsed time.

## FP5 - GHCR container image

**Status:** landed with this change.

### Why

CI shipped release binaries only, so a consumer had to build its own image and could not pin an
exact, provenance carrying engine version.

### What changed

`build.yml` gains a `docker` job which publishes `ghcr.io/strattonlead/smtp4dev:<version>-p<n>`
for `linux/amd64` and `linux/arm64` on `v*` tags. Tags are immutable and exact - **no `latest`,
no `main`** - because a moving engine tag makes a downstream deployment irreproducible. The commit
and ref are recorded as image labels.

`Dockerfile.linux` exposed 80/25/143/110, which predates FP3. It now exposes 80, 587, 465, 2525,
25, 143, 993, 110 and 995.

## FP6 - Nightly workflow for the tests the gate excludes

**Status:** landed with this change.

### Why

`build.yml` excludes `E2E.WebUI`, which needs Playwright browsers, and `MemoryLeakStress`, which
runs for minutes. Neither is a reason to never run them: this fork is never contributed upstream,
so its own tests are the only regression net there is, and the two exclusions are the parts of
that net with holes in them. They are also exactly the tests most likely to catch an upstream
refactor going wrong during a rebase.

### What changed

`.github/workflows/nightly.yml` runs both sets on a schedule and on demand, in a matrix so one
failing does not hide the other. The WebUI job installs Chromium first.

### Owner

The Deadletter maintainer of this fork. A red nightly is triaged before the next rebase, not
after: the rebase checklist in the Deadletter spec §3.3 requires a green nightly before a new
patch tag is published.
