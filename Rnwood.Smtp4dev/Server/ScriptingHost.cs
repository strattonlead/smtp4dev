using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Esprima;
using Esprima.Ast;
using Jint;
using Jint.Native;
using Jint.Runtime;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Microsoft.Extensions.Options;
using Rnwood.Smtp4dev.DbModel;
using Rnwood.Smtp4dev.Server.Settings;
using Rnwood.SmtpServer;
using Rnwood.SmtpServer.Extensions.Auth;
using Serilog;
using StreamLib;

namespace Rnwood.Smtp4dev.Server;

public class ScriptingHost
{
    private readonly ILogger log = Log.ForContext<ScriptingHost>();



    private IOptionsMonitor<RelayOptions> relayOptions;
    private IOptionsMonitor<Settings.ServerOptions> serverOptions;

    public ScriptingHost(IOptionsMonitor<RelayOptions> relayOptions, IOptionsMonitor<Settings.ServerOptions> serverOptions)
    {
        this.relayOptions = relayOptions;
        this.serverOptions = serverOptions;
        this.relayOptions.OnChange(_ => ParseScripts(relayOptions.CurrentValue, serverOptions.CurrentValue));
        this.serverOptions.OnChange(_ => ParseScripts(relayOptions.CurrentValue, serverOptions.CurrentValue));
        ParseScripts(relayOptions.CurrentValue, serverOptions.CurrentValue);
    }

    private void ParseScript(string type, string expression, ref Script script, ref string source)
    {
        if (source != expression)
        {
            expression = expression ?? "";

            if (string.IsNullOrWhiteSpace(expression))
            {
                script = null;
                source = expression;
            }
            else
            {
                log.Debug("Parsing {type} - {expression}", type, expression);
                var parser = new JavaScriptParser();

                try
                {
                    script = parser.ParseScript(expression);
                    source = expression;
                }
                catch (Esprima.ParserException e)
                {
                    log.Error("Error parsing {type} - {error}", type, e.Message);
                    script = null;
                    source = "";
                }
            }


        }
    }

    private void ParseScripts(RelayOptions relayOptionsCurrentValue, Settings.ServerOptions serverOptionsCurrentValue)
    {
        ParseScript("AutomaticRelayExpression", relayOptionsCurrentValue.AutomaticRelayExpression, ref shouldRelayScript, ref shouldRelaySource);
        ParseScript("CredentialsValidationExpression", serverOptionsCurrentValue.CredentialsValidationExpression, ref credValidationScript,
            ref credValidationSource);
        ParseScript("RecipientValidationExpression", serverOptionsCurrentValue.RecipientValidationExpression, ref recipValidationScript,
            ref recipValidationSource);
        ParseScript("MessageValidationExpression", serverOptionsCurrentValue.MessageValidationExpression, ref messageValidationScript,
            ref messageValidationSource);
        ParseScript("CommandValidationExpression", serverOptionsCurrentValue.CommandValidationExpression, ref commandValidationScript,
    ref commandValidationSource);
    }

    private string shouldRelaySource;
    private Script shouldRelayScript;
    
    private string credValidationSource;
    private Script credValidationScript;
    
    private string recipValidationSource;
    private Script recipValidationScript;

    private string messageValidationSource;
    private Script messageValidationScript;

    private string commandValidationSource;
    private Script commandValidationScript;

    public bool HasValidateMessageExpression { get => this.messageValidationScript != null; }
    public bool HasValidateCommandExpression { get => this.commandValidationScript != null; }

    private void AddStandardApi(Engine jsEngine, IConnection connection)
    {
        jsEngine.SetValue("error", (Action<int?, string>)((code, message) => throw new SmtpServerException(new SmtpResponse(code ?? (int)StandardSmtpResponseCode.TransactionFailed, message ?? ""))));

        jsEngine.SetValue("delay", (Func<double, bool>)(seconds => Delay(seconds, connection)));

        jsEngine.SetValue("random", (Func<int, int, int>)((minValue, maxValue) => Random.Shared.Next(minValue, maxValue)));

        jsEngine.SetValue("disconnect", (Action)(() => throw new ConnectionUnexpectedlyClosedException("Closed by scripting expression")));

        jsEngine.SetValue("throttle", (Func<int, bool>)(bps =>
        {
            connection.ApplyStreamFilter((s) => Task.FromResult<Stream>(new ThrottledStream(s, bps, throttleWrites: true, throttleReads: true))).Wait();
            return true;
        }));
        ;

    }

    /// <summary>
    /// How often a delay checks whether its connection is still there. Short enough that a
    /// disconnect frees the thread promptly, long enough that a delay costs no measurable CPU.
    /// </summary>
    private static readonly TimeSpan DelayPollInterval = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// Waits for <paramref name="seconds"/>, or until the connection goes away, whichever comes
    /// first. <c>-1</c> means "until the connection goes away".
    /// </summary>
    /// <remarks>
    /// This polls rather than waiting on the connection closed event, because while an expression
    /// is running nothing is reading the socket, so a client which disconnects mid delay raises no
    /// event at all.
    ///
    /// A delay which outlives its connection parks an OS thread for nothing, and expressions are
    /// global: one tenant's timeout rule is evaluated on every tenant's commands, so the threads
    /// it parks are everyone's. That is why this is bounded by the connection rather than only by
    /// the requested duration.
    /// </remarks>
    private bool Delay(double seconds, IConnection connection)
    {
        Interlocked.Increment(ref blockedConnections);

        try
        {
            return DelayCore(seconds, connection);
        }
        finally
        {
            Interlocked.Decrement(ref blockedConnections);
        }
    }

    private static int blockedConnections;

    /// <summary>
    /// How many connections are currently parked inside <c>delay()</c>.
    ///
    /// This cannot be inferred from expression evaluation latency, because for a blocking rule the
    /// block <em>is</em> the latency - the metric that would warn you is the one the fault makes
    /// look normal. Since the expressions are global, one client's timeout rule parks threads
    /// belonging to every client, so this is the number that says how close the whole server is to
    /// thread exhaustion.
    /// </summary>
    public static int BlockedConnections => Volatile.Read(ref blockedConnections);

    private bool DelayCore(double seconds, IConnection connection)
    {
        bool indefinite = seconds == -1;
        DateTime deadline = indefinite ? DateTime.MaxValue : DateTime.UtcNow.AddSeconds(seconds);

        while (true)
        {
            if (connection != null && !connection.IsConnected)
            {
                log.Debug("delay({seconds}) returned early because the connection closed", seconds);
                return true;
            }

            TimeSpan remaining = indefinite ? DelayPollInterval : deadline - DateTime.UtcNow;

            if (remaining <= TimeSpan.Zero)
            {
                return true;
            }

            Thread.Sleep(remaining < DelayPollInterval ? remaining : DelayPollInterval);
        }
    }

    public IReadOnlyCollection<string> GetAutoRelayRecipients(ApiModel.Message message, string recipient, ApiModel.Session session)
    {
        if (shouldRelayScript == null)
        {
            return Array.Empty<string>();
        }
        var jsEngine = CreateEngineWithStandardApi(null);

        jsEngine.SetValue("recipient", recipient);
        jsEngine.SetValue("message", message);
        jsEngine.SetValue("session", session);

        try
        {
            JsValue result = jsEngine.Evaluate(shouldRelayScript);

            List<string> relayRecipients = new List<string>();
            if (result.IsNull())
            {

            }
            else if (result.IsString())
            {
                if (result.AsString() != String.Empty)
                {
                    relayRecipients.Add(result.AsString());
                }
            }
            else if (result.IsArray())
            {
                relayRecipients.AddRange(result.AsArray().Select(v => v.AsString()));
            }
            else if (result.AsBoolean())
            {
                relayRecipients.Add(recipient);
            }

            log.Information("AutomaticRelayExpression: (message: {messageId}, recipient: {recipient}, session: {sessionId}) => {result} => {relayRecipients}", message.Id, recipient,
                session.Id, result, relayRecipients);

            return relayRecipients;

        }
        catch (ConnectionUnexpectedlyClosedException)
        {
            throw;
        }
        catch (SmtpServerException)
        {
            throw;
        }
        catch (JavaScriptException ex)
        {
            log.Error("Error executing AutomaticRelayExpression : {error}", ex.Error);
            return Array.Empty<string>();
        }
        catch (Exception ex)
        {
            log.Error("Error executing AutomaticRelayExpression : {error}", ex.ToString());
            return Array.Empty<string>();
        }

    }

    private Engine CreateEngineWithStandardApi(IConnection connection)
    {
        var result = new Engine();
        AddStandardApi(result, connection);
        return result;
    }

    public AuthenticationResult? ValidateCredentials(ApiModel.Session session, IAuthenticationCredentials credentials, IConnection connection)
    {
        if (credValidationScript == null)
        {
            return null;
        }

        Engine jsEngine = CreateEngineWithStandardApi(connection);

        jsEngine.SetValue("credentials", credentials);
        jsEngine.SetValue("session", session);

        try
        {
            JsValue result = jsEngine.Evaluate(credValidationScript);

            // null or undefined means the expression has no opinion about these credentials, so
            // the normal user and password check decides.
            //
            // Without this the hook is all or nothing: any expression at all replaces password
            // validation for the whole server, so an expression which rejects one account has to
            // return true for everyone else - and true here means "authenticated", not "carry on
            // checking". An expression written to fail one login would silently accept every
            // other login with any password.
            if (result.IsNull() || result.IsUndefined())
            {
                log.Information("CredentialValidationExpression: (credentials: {credentials}, session: {session.Id}) => no opinion", credentials,
                    session.Id);

                return null;
            }

            bool success = result.AsBoolean();

            log.Information("CredentialValidationExpression: (credentials: {credentials}, session: {session.Id}) => {result} => {success}", credentials,
                session.Id, result, success);

            return success ? AuthenticationResult.Success : AuthenticationResult.Failure;

        }
        catch (ConnectionUnexpectedlyClosedException)
        {
            throw;
        }
        catch (SmtpServerException)
        {
            throw;
        }
        catch (JavaScriptException ex)
        {
            log.Error("Error executing CredentialValidationExpression : {error}", ex.Error);
            return AuthenticationResult.TemporaryFailure;
        }
        catch (Exception ex)
        {
            log.Error("Error executing CredentialValidationExpression : {error}", ex.ToString());
            return AuthenticationResult.TemporaryFailure;
        }
    }

    public bool ValidateRecipient(ApiModel.Session session, string recipient, IConnection connection)
    {
        if (recipValidationScript == null)
        {
            return true;
        }

        Engine jsEngine = CreateEngineWithStandardApi(connection);

        jsEngine.SetValue("recipient", recipient);
        jsEngine.SetValue("session", session);

        try
        {
            JsValue result = jsEngine.Evaluate(recipValidationScript);

            bool success = result.AsBoolean();

            log.Information("RecipientValidationExpression: (recipient: {recipient}, session: {session.Id}) => {result} => {success}", recipient,
                session.Id, result, success);

            return success;

        }
        catch (ConnectionUnexpectedlyClosedException)
        {
            throw;
        }
        catch (SmtpServerException)
        {
            throw;
        }
        catch (JavaScriptException ex)
        {
            log.Error("Error executing RecipientValidationExpression : {error}", ex.Error);
            return false;
        }
        catch (Exception ex)
        {
            log.Error("Error executing RecipientValidationExpression : {error}", ex.ToString());
            return false;
        }
    }

    internal SmtpResponse ValidateCommand(SmtpCommand command, ApiModel.Session session, IConnection connection)
    {
        if (commandValidationScript == null)
        {
            return null;
        }

        Engine jsEngine = CreateEngineWithStandardApi(connection);

        jsEngine.SetValue("command", command);
        jsEngine.SetValue("session", session);

        try
        {
            JsValue result = jsEngine.Evaluate(commandValidationScript);

            SmtpResponse response;

            if (result.IsNull() || result.IsUndefined())
            {
                response = null;
            }
            else if (result.IsNumber())
            {
                response = new SmtpResponse((int)result.AsNumber(), "Command rejected by CommandValidationExpression");
            }
            else if (result.IsString())
            {
                response = new SmtpResponse(StandardSmtpResponseCode.TransactionFailed, result.AsString());
            }
            else
            {
                response = result.AsBoolean() ? null : new SmtpResponse(StandardSmtpResponseCode.TransactionFailed, "Message rejected by CommandValidationExpression");
            }

            log.Information("CommandValidationExpression: (command: {command}, session: {session.Id}) => {result} => {success}", command,
                session.Id, result, response?.Code.ToString() ?? "Success");

            return response;

        }
        catch (ConnectionUnexpectedlyClosedException)
        {
            throw;
        }
        catch (SmtpServerException ex)
        {
            return ex.SmtpResponse;
        }
        catch (JavaScriptException ex)
        {
            log.Error("Error executing CommandValidationExpression : {error}", ex.Error);
            return new SmtpResponse(StandardSmtpResponseCode.TransactionFailed, "CommandValidationExpression failed");
        }
        catch (Exception ex)
        {
            log.Error("Error executing CommandValidationExpression : {error}", ex.ToString());
            return new SmtpResponse(StandardSmtpResponseCode.TransactionFailed, "CommandValidationExpression failed");
        }
    }

    internal SmtpResponse ValidateMessage(ApiModel.Message message, ApiModel.Session session, IConnection connection)
    {
        if (messageValidationScript == null)
        {
            return null;
        }

        Engine jsEngine = CreateEngineWithStandardApi(connection);

        jsEngine.SetValue("message", message);
        jsEngine.SetValue("session", session);

        try
        {
            JsValue result = jsEngine.Evaluate(messageValidationScript);

            SmtpResponse response;

            if (result.IsNull() || result.IsUndefined())
            {
                response = null;
            }
            else if (result.IsNumber())
            {
                response = new SmtpResponse((int)result.AsNumber(), "Message rejected by MessageValidationExpression");
            }
            else if (result.IsString())
            {
                response = new SmtpResponse(StandardSmtpResponseCode.TransactionFailed, result.AsString());
            }
            else
            {
                response = result.AsBoolean() ? null : new SmtpResponse(StandardSmtpResponseCode.TransactionFailed, "Message rejected by MessageValidationExpression");
            }

            log.Information("MessageValidationExpression: (message: {message}, session: {session.Id}) => {result} => {success}", message,
                session.Id, result, response?.Code.ToString() ?? "Success");

            return response;

        }
        catch (ConnectionUnexpectedlyClosedException)
        {
            throw;
        }
        catch (SmtpServerException ex)
        {
            return ex.SmtpResponse;
        }
        catch (JavaScriptException ex)
        {
            log.Error("Error executing MessageValidationExpression : {error}", ex.Error);
            return new SmtpResponse(StandardSmtpResponseCode.TransactionFailed, "MessageValidationExpression failed");
        }
        catch (Exception ex)
        {
            log.Error("Error executing MessageValidationExpression : {error}", ex.ToString());
            return new SmtpResponse(StandardSmtpResponseCode.TransactionFailed, "MessageValidationExpression failed");
        }
    }
}