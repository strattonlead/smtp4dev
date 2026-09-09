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
| FP3 | Multi-listener SMTP and IMAP TLS | Landed `v3.3.0-p2` | `ServerOptions.cs`, `SmtpListenerOptions.cs`, `ImapListenerOptions.cs`, `ServerOptionsSource.cs`, `CertificateHelper.cs`, `Smtp4devServer.cs`, `ImapServer.cs` + 5 test files |
| FP4 | Cancellable `delay()` | Landed `v3.3.0-p2` | `ScriptingHost.cs`, `IConnection.cs`, `Connection.cs`, `IConnectionChannel.cs`, `TcpClientConnectionChannel.cs`, `TestMocks.cs` + 1 test file |
| FP5 | GHCR container image | Landed `v3.3.0-p2` | `.github/workflows/build.yml`, `Dockerfile.linux` |
| FP6 | Nightly workflow for excluded tests | Landed `v3.3.0-p2` | `.github/workflows/nightly.yml` |
| FP7 | Remove the upstream CLA workflow | Landed `v3.3.0-p2` | `.github/workflows/cla.yml` (deleted) |
| FP8 | Message provenance: mailbox and session on the API model | Landed `v3.3.0-p3` | `ApiModel/Message.cs`, `Data/MessagesRepository.cs` + 1 test file |
| FP9 | Reject a settings write whose expressions do not parse | Landed `v3.3.0-p4` | `Controllers/ServerController.cs` + 1 test file |
| FP10 | `blocked_connections` gauge and a metrics endpoint | Landed `v3.3.0-p4` | `ScriptingHost.cs`, `Controllers/MetricsController.cs`, `ApiModel/Metrics.cs` + 1 test file |
| FP11 | The authenticated user on the scripting session handle | Landed `v3.3.0-p5` | `ApiModel/Session.cs`, `Smtp4devServer.cs` + 1 test file |
| FP12 | A credentials expression may have no opinion | Landed `v3.3.0-p6` | `ScriptingHost.cs` + 1 test file |

## Versioning

Release tags are `v<upstream-version>-p<patchlevel>`. The upstream version identifies the
smtp4dev release the patch branch sits on; the patch level increments whenever our diff changes
against that same upstream version. Current: `v3.3.0-p6`, published as release binaries for six
runtimes and as `ghcr.io/strattonlead/smtp4dev:3.3.0-p6` for `linux/amd64` and `linux/arm64`.

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

**Status:** landed in `v3.3.0-p2`.

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

Only a listener which asked for TLS is given the certificate. `IMAP_Session` advertises
`STARTTLS` whenever its certificate is non null, independently of the binding's `SslMode`, so
passing the certificate to every binding would make a plaintext listener offer an upgrade its
configuration never asked for - and a client connecting with `SecureSocketOptions.Auto` takes it.
`ImapServerPlaintextTests` pins this down.

A listener which asks for TLS with no resolvable certificate now throws rather than binding in
plaintext.

### Test set

`ServerOptionsListenerTests` covers the resolvers and the certificate gate,
`Smtp4devServerListenerTests` the SMTP listener lifecycle and both TLS handshakes,
`ImapServerTlsTests` implicit TLS and STARTTLS on IMAP, `ImapServerPlaintextTests` that a
plaintext listener still offers neither, `ListenerConfigTests` the option classification, and
`SettingsFileListenerRoundTripTests` that the listener collections survive the settings file
round trip which every API write performs.

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

**Status:** landed in `v3.3.0-p2`.

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

**Status:** landed in `v3.3.0-p2`.

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

**Status:** landed in `v3.3.0-p2`.

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

## FP7 - Remove the upstream CLA workflow

**Status:** landed in `v3.3.0-p2`.

### Why

`.github/workflows/cla.yml` is upstream's contributor licence agreement bot. It points at
`rnwood/smtp4dev`'s CLA document and stores signatures on a `clas` branch which does not exist
here, so it failed on every pull request in this fork with "Committers of pull request N have to
sign the CLA".

No CLA applies to this fork. That agreement is triggered by opening a pull request against
`rnwood/smtp4dev`, and this fork is never contributed upstream. Meanwhile a check which is red on
every pull request regardless of the change is worse than no check: the fork's own CI is the only
regression net there is, and a permanently failing job trains everyone to ignore the summary it
appears in.

`CLA.md` is left in place - it is part of the upstream history and costs nothing.

### What changed

The workflow file is deleted. If this fork ever does contribute upstream, the contributor signs
the CLA on the upstream pull request, which is where the bot actually runs.

## FP8 - Message provenance

**Status:** landed in `v3.3.0-p3`.

### Why

`GET /api/messages/{id}` projected neither the mailbox a message was delivered to nor the SMTP
session it arrived on, although `DbModel.Message` has a navigation property for both.

Two consequences, both of which matter to anything serving more than one tenant:

- **A message id proves nothing about ownership.** The engine resolves an id across every mailbox,
  so a front end holding one had no way to tell whose mailbox it landed in except by listing every
  mailbox that caller owns and looking for the id. That is correct but O(all their mail) per read.
- **A transcript could not be reached from a message.** The session log endpoint takes a session
  id, and nothing in the message response carried one, so there was no path from "this message
  looks wrong" to "here is the SMTP conversation that produced it". That is the whole point of
  keeping transcripts.

### What changed

`ApiModel.Message` gains `MailboxName` and `SessionId`, projected from relations which already
existed. `MessagesRepository.GetAllMessages` includes both, which is what `TryGetMessageById`
reads through - the mailbox was already included on two other paths but not on the one the API
actually uses.

`MessageSummary` is deliberately unchanged: the list projection is deliberately narrow, and a
caller which needs provenance is asking for one message.

### Rebase notes

The include lives in `GetAllMessages` rather than at the call site, so an upstream refactor which
routes message reads through a different query will drop it silently. `MessageProvenanceTests`
asserts both fields end to end through the repository, so it catches that.

## FP9 - Reject a settings write whose expressions do not parse

**Status:** landed in `v3.3.0-p4`.

### Why

A settings write carrying unparseable JavaScript used to succeed. `ScriptingHost.ParseScript` then
failed silently: it logged, set the script to null, and the hook was disabled from that moment on.
The caller got a 200 and no indication that its rules had stopped applying.

Because the validation expressions are a single global value per hook, that did not disable the
hook only for the client which made the bad write - it disabled it for **every** client of the
server. A multi tenant front end therefore had no safe way to install a rule: a rendering bug in
one tenant's rule silently turned off fault injection for all of them.

The obvious alternative is a validate-then-write endpoint, but that leaves a window between the
check and the write. Refusing the write itself has no window.

### What changed

`ServerController.UpdateServer` parses all four validation expressions before writing anything, and
returns 400 naming the offending property and the parser's message. It uses the same
`Esprima.JavaScriptParser` `ScriptingHost` uses, in the same process, so an expression which passes
this check cannot fail there.

### Rebase notes

`ExpressionValidationTests.EveryValidationExpressionOnTheApiModelIsChecked` fails if a fifth
expression is added to the API model without being validated, which is exactly how the silent
failure would come back.

## FP10 - Blocked connection gauge

**Status:** landed in `v3.3.0-p4`.

### Why

`delay()` parks the connection's thread. Expression evaluation latency cannot detect that, because
for a blocking rule the block *is* the latency - the metric which would warn you is the one the
fault makes look normal. And since expressions are global, one client's `timeout` rule parks
threads belonging to every client, so the interesting number is server wide.

### What changed

`ScriptingHost` counts connections currently inside `delay()` and exposes
`ScriptingHost.BlockedConnections`. A new `GET /api/metrics` returns it.

The metric lives in its own `ApiModel.Metrics`, not on `ApiModel.Server`: that object is settings,
which callers read, edit and post back, and a number which changes on its own does not belong in
something round tripped.

## FP11 - The authenticated user on the scripting session handle

**Status:** landed in `v3.3.0-p5`.

### Why

Validation expressions are a single global value per hook, so an expression written for one
account is evaluated on every account's connections. The only thing that can keep them apart is a
handle saying whose connection is being looked at.

At AUTH that handle is `credentials.Username`, and at RCPT it is `recipient`. At every other hook
there was nothing: the `session` object handed to an expression is built from the **stored**
session, and `DbModel.Session` never recorded who authenticated. `ISession` on the live connection
has had `Authenticated` and `AuthenticationCredentials` all along; the information was simply
dropped on the way to the expression.

The practical consequence is that a rule like "make MAIL FROM fail for this one account" could not
be written at all. Any attempt hit every account on the server, which for a multi tenant front end
is not a limitation but a data leak with side effects.

### What changed

`ApiModel.Session` gains `AuthenticatedUser`, and `Smtp4devServer` fills it from the live
connection at each point it builds a session for the scripting host. It is deliberately not
persisted: it belongs to the connection, not to the stored record, and writing it to the database
would mean a schema migration for something already in memory.

Before AUTH the value is null, which is the honest answer - an expression scoped to an account
must not match a connection which has not yet proved it owns that account. Expressions should
therefore compare it with `===` against a specific username and let null fall through to "no rule
applies".

### Rebase notes

`ScriptingSessionScopeTests` drives a real SMTP conversation for two accounts and asserts that a
rule naming one of them rejects only that one. If an upstream refactor rebuilds the session handle
somewhere new and forgets to attach the user, that test fails rather than the scoping silently
widening to everybody - which is the failure mode that matters, and the one that would otherwise
look like the rule simply working.

## FP12 - A credentials expression may have no opinion

**Status:** landed in `v3.3.0-p6`.

### Why

`ValidateCredentials` returns `AuthenticationResult?`, and the caller already treats null as "no
opinion, do the normal check". The expression could never produce null: whatever it evaluated to
was coerced with `AsBoolean()` and turned into Success or Failure.

That made the hook all or nothing. The moment any expression exists it replaces password
validation for the entire server, so an expression which rejects one account has to return
something for every other account - and the only thing it can return is a boolean, where true
means **authenticated**, not "carry on checking".

The consequence is worth stating plainly. An expression written to fail one login, of the obvious
shape

```js
credentials.Username === 'blocked' ? false : true
```

**silently accepts every other login with any password at all.** It reads like a targeted rule and
behaves like turning authentication off. For a server hosting more than one account that is not a
misfeature, it is an outage of the only thing keeping accounts apart.

This was found by running a multi tenant front end against the engine: installing a single
"reject this account's login" rule made the server accept a deliberately wrong password for a
different account.

### What changed

A `null` or `undefined` result now means the expression has no opinion, and the normal user and
password check decides. Anything else still coerces to a boolean exactly as before, so an
expression which returns true or false keeps its old meaning.

The safe shape for a targeted rule is now:

```js
credentials.Username === 'blocked' ? false : null
```

### Rebase notes

`CredentialsExpressionTests` drives real SMTP for three cases: the named account is rejected,
another account authenticates normally, and a wrong password for that other account is still
refused. The third is the one that matters - it is the assertion that fails if the null handling
is lost, and its failure mode is the server accepting anything.
