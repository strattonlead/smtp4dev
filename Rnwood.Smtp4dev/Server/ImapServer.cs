using LumiSoft.Net;
using LumiSoft.Net.IMAP.Server;
using LumiSoft.Net.MIME;
using Microsoft.Extensions.Options;
using Rnwood.Smtp4dev.DbModel;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Reactive.Linq;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using System.Net.NetworkInformation;
using Serilog;
using Microsoft.Extensions.Hosting;
using System.Threading;
using Microsoft.AspNetCore.Http;
using Org.BouncyCastle.Utilities.Net;
using Rnwood.Smtp4dev.Server.Settings;
using MailKit.Net.Imap;

namespace Rnwood.Smtp4dev.Server
{
    public partial class ImapServer : IHostedService
    {
        public ImapServer(IOptionsMonitor<ServerOptions> serverOptions, ScriptingHost scriptingHost, IServiceScopeFactory serviceScopeFactory)
        {
            this.serverOptions = serverOptions;
            this.serviceScopeFactory = serviceScopeFactory;
            this.scriptingHost = scriptingHost;

            IDisposable eventHandler = null;
            var obs = Observable.FromEvent<ServerOptions>(e => eventHandler = serverOptions.OnChange(e), e => eventHandler.Dispose());
            obs.Throttle(TimeSpan.FromMilliseconds(100)).Subscribe(OnServerOptionsChanged);

        }

        /// <summary>
        /// The subset of <see cref="ServerOptions"/> which is read while the IMAP listener is being
        /// created, including the certificate options which
        /// <see cref="CertificateHelper.GetTlsCertificate"/> resolves, so that a certificate
        /// renewal takes effect. A change to one of these can only take effect by restarting the
        /// listener. Every other option is read at the point of use, so changing it must not
        /// disturb connections which are already established.
        ///
        /// Unlike SMTP, the listener set is part of this projection rather than being reconciled
        /// entry by entry: LumiSoft's IMAP_Server owns its whole bindings array and has no
        /// per binding lifecycle, so adding or removing an IMAP listener restarts all of them.
        ///
        /// Keep this in sync when adding an option which affects the listener.
        /// </summary>
        internal readonly record struct ImapListenerConfig(
            string Listeners,
            string BindAddress,
            bool AllowRemoteConnections,
            bool DisableIPv6,
            string HostName,
            string TlsCertificate,
            string TlsCertificatePrivateKey,
            string TlsCertificateStoreThumbprint,
            string TlsCertificatePassword)
        {
            public static ImapListenerConfig From(ServerOptions options) => new(
                DescribeListeners(options),
                options.BindAddress,
                options.AllowRemoteConnections,
                options.DisableIPv6,
                options.HostName,
                options.TlsCertificate,
                options.TlsCertificatePrivateKey,
                options.TlsCertificateStoreThumbprint,
                options.TlsCertificatePassword);

            /// <summary>
            /// The listener set as a value comparable string. An array member would compare by
            /// reference, so every settings write would look like a listener change and restart the
            /// listener.
            /// </summary>
            private static string DescribeListeners(ServerOptions options) =>
                string.Join(";", options.ResolveImapListeners().Select(listener => $"{listener.Port}:{listener.TlsMode}"));
        }

        private void OnServerOptionsChanged(ServerOptions serverOptions)
        {
            if (ImapListenerConfig.From(serverOptions) == this.lastListenerConfig)
            {
                log.Debug("ServerOptions changed but no IMAP listener settings were affected. Not restarting the server.");
                return;
            }

            if (IsRunning)
            {
                log.Information("IMAP listener configuration changed. Restarting server...");
                Stop();

                TryStart();
            }
        }

        public bool IsRunning
        {
            get
            {
                return imapServer?.IsRunning ?? false;
            }
        }

        public async void TryStart()
        {
            this.lastListenerConfig = ImapListenerConfig.From(serverOptions.CurrentValue);

            ServerOptions options = serverOptions.CurrentValue;
            IReadOnlyList<ImapListenerOptions> listeners = options.ResolveImapListeners();

            if (listeners.Count == 0)
            {
                log.Information("IMAP server disabled - no port configured");
                return;
            }

            X509Certificate2 certificate = CertificateHelper.GetTlsCertificate(options, log);

            List<IPBindInfo> bindings = new List<IPBindInfo>();

            // Check if a specific bind address is configured
            System.Net.IPAddress bindAddress = null;
            if (!string.IsNullOrWhiteSpace(options.BindAddress))
            {
                if (!System.Net.IPAddress.TryParse(options.BindAddress, out bindAddress))
                {
                    log.Error("Invalid IMAP bind address configured: {bindAddress}", options.BindAddress);
                    throw new ArgumentException($"Invalid bind address: {options.BindAddress}");
                }
            }

            foreach (ImapListenerOptions listener in listeners)
            {
                SslMode sslMode = ToSslMode(listener.TlsMode);

                if (sslMode != SslMode.None && certificate == null)
                {
                    //Binding in plaintext where TLS was asked for would hand credentials and
                    //password reset links to anyone watching, so refuse the listener instead.
                    throw new InvalidOperationException(
                        $"IMAP listener on port {listener.Port} asks for TLS mode {listener.TlsMode} but no certificate could be resolved.");
                }

                foreach (System.Net.IPAddress address in BindAddressesFor(options, bindAddress))
                {
                    bindings.Add(new IPBindInfo(options.HostName, BindInfoProtocol.TCP, address, listener.Port, sslMode, certificate));
                }
            }

            imapServer = new IMAP_Server()
            {

                Bindings = bindings.ToArray(),
                GreetingText = "smtp4dev"
            };
            imapServer.SessionCreated += (o, ea) => new SessionHandler(ea.Session, scriptingHost, serverOptions, this.serviceScopeFactory);


            var errorTcs = new TaskCompletionSource<Error_EventArgs>();
            imapServer.Error += (s, ea) =>
            {
                if (!errorTcs.Task.IsCompleted)
                {
                    errorTcs.TrySetResult(ea);
                }
            };

            var startedTcs = new TaskCompletionSource<EventArgs>();
            imapServer.Started += (s, ea) => startedTcs.SetResult(ea);

            imapServer.Start();

            var errorTask = errorTcs.Task;
            var startedTask = startedTcs.Task;

            int index = Task.WaitAny(startedTask, errorTask, Task.Delay(TimeSpan.FromSeconds(30)));

            if (index == 1)
            {
                log.Error(errorTask.Result.Exception, "IMAP server failed to start. Port: {port}, BindAddress: {bindAddress}, ExceptionType: {exceptionType}",
                    serverOptions.CurrentValue.ImapPort, serverOptions.CurrentValue.BindAddress ?? "Any", 
                    errorTask.Result.Exception.GetType().Name);
            }
            else if (index == 2)
            {
                log.Error("IMAP server failed to start - timeout after 30 seconds. Port: {port}", 
                    serverOptions.CurrentValue.ImapPort);

                try
                {
                    imapServer.Stop();
                }
                catch { }
            }
            else
            {
                //Race condition in IMAP server - it fires the running event before this is all populated (or replaced from prev start).
                while (imapServer.ListeningPoints.Length < imapServer.Bindings.Length && !imapServer.ListeningPoints.All(lp =>
                {
                    try
                    {
                        return lp.Socket.LocalEndPoint != null;
                    }
                    catch (ObjectDisposedException)
                    {
                        return false;
                    }
                }))
                {
                    await Task.Delay(100);
                }

                foreach (var lp in imapServer.ListeningPoints)
                {
                    var ep = ((IPEndPoint)lp.Socket.LocalEndPoint);
                    int port = ep.Port;
                    log.Information("IMAP Server is listening on port {port} ({address})", port, ep.Address);
                }

            }
        }

        public void Stop()
        {
            imapServer?.Stop();
            imapServer = null;
        }

        /// <summary>
        /// The addresses one listener binds to, in the order the previous implementation used them:
        /// a configured bind address wins, otherwise IPv6 first with IPv4 as the fallback which
        /// LumiSoft's TCP_Server error handling relies on.
        /// </summary>
        private static IEnumerable<System.Net.IPAddress> BindAddressesFor(ServerOptions options, System.Net.IPAddress bindAddress)
        {
            if (bindAddress != null)
            {
                yield return bindAddress;
                yield break;
            }

            if (options.AllowRemoteConnections)
            {
                if (!options.DisableIPv6)
                {
                    yield return System.Net.IPAddress.IPv6Any;
                }

                yield return System.Net.IPAddress.Any;
                yield break;
            }

            yield return System.Net.IPAddress.Loopback;

            if (!options.DisableIPv6)
            {
                yield return System.Net.IPAddress.IPv6Loopback;
            }
        }

        private static SslMode ToSslMode(TlsMode tlsMode) => tlsMode switch
        {
            TlsMode.ImplicitTls => SslMode.SSL,
            TlsMode.StartTls => SslMode.TLS,
            _ => SslMode.None
        };

        /// <summary>
        /// The endpoints every running binding is listening on. Ports are resolved, so a listener
        /// configured on port 0 reports the port the OS gave it.
        /// </summary>
        internal IPEndPoint[] ListeningEndpoints
        {
            get
            {
                try
                {
                    return imapServer?.ListeningPoints
                        .Select(lp => lp.Socket?.LocalEndPoint as IPEndPoint)
                        .Where(endpoint => endpoint != null)
                        .ToArray() ?? [];
                }
                catch (ObjectDisposedException)
                {
                    //The listener is being replaced.
                    return [];
                }
            }
        }

        private IMAP_Server imapServer;
        private IOptionsMonitor<ServerOptions> serverOptions;
        private ImapListenerConfig lastListenerConfig;
        private readonly IServiceScopeFactory serviceScopeFactory;
        private readonly ScriptingHost scriptingHost;
        private readonly ILogger log = Log.ForContext<ImapServer>();

        private void Logger_WriteLog(object sender, LumiSoft.Net.Log.WriteLogEventArgs e)
        {
            log.Information(e.LogEntry.Text);
        }

        Task IHostedService.StartAsync(CancellationToken cancellationToken)
        {
            this.TryStart();
            return Task.CompletedTask;

        }

        Task IHostedService.StopAsync(CancellationToken cancellationToken)
        {
            Task.Run(() => this.Stop());
            return Task.CompletedTask;
        }
    }
}
