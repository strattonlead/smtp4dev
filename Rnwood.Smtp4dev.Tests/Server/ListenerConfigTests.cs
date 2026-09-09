using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Rnwood.Smtp4dev.Server.Settings;
using Xunit;
using ImapListenerConfig = Rnwood.Smtp4dev.Server.ImapServer.ImapListenerConfig;
using SmtpListenerConfig = Rnwood.Smtp4dev.Server.Smtp4devServer.SmtpListenerConfig;
using TlsMode = Rnwood.Smtp4dev.Server.TlsMode;

namespace Rnwood.Smtp4dev.Tests.Server
{
    /// <summary>
    /// Settings changes are classified into three buckets, and getting the classification wrong is
    /// either a dropped connection or an option which silently never takes effect. These tests pin
    /// down which bucket every <see cref="ServerOptions"/> member is in:
    ///
    /// - shared listener options, which can only take effect by recreating every listener,
    /// - listener set options, which start and stop individual listeners,
    /// - everything else, which is read at the point of use and must disturb nothing.
    ///
    /// Adding a member to ServerOptions without classifying it here fails
    /// <see cref="EveryServerOption_IsClassified"/>.
    /// </summary>
    public class ListenerConfigTests
    {
        /// <summary>
        /// Projection member name to the ServerOptions members it stands for.
        /// </summary>
        private static readonly Dictionary<string, string[]> SmtpSharedProjection = new()
        {
            ["BindAddress"] = [nameof(ServerOptions.BindAddress)],
            ["AllowRemoteConnections"] = [nameof(ServerOptions.AllowRemoteConnections)],
            ["DisableIPv6"] = [nameof(ServerOptions.DisableIPv6)],
            ["HostName"] = [nameof(ServerOptions.HostName)],
            ["SslProtocols"] = [nameof(ServerOptions.SslProtocols)],
            ["TlsCipherSuites"] = [nameof(ServerOptions.TlsCipherSuites)],
            ["MaxMessageSize"] = [nameof(ServerOptions.MaxMessageSize)],
            ["AuthenticationRequired"] = [nameof(ServerOptions.AuthenticationRequired)],
            ["SmtpEnabledAuthTypesWhenNotSecureConnection"] = [nameof(ServerOptions.SmtpEnabledAuthTypesWhenNotSecureConnection)],
            ["SmtpEnabledAuthTypesWhenSecureConnection"] = [nameof(ServerOptions.SmtpEnabledAuthTypesWhenSecureConnection)],
            ["TlsCertificate"] = [nameof(ServerOptions.TlsCertificate)],
            ["TlsCertificatePrivateKey"] = [nameof(ServerOptions.TlsCertificatePrivateKey)],
            ["TlsCertificateStoreThumbprint"] = [nameof(ServerOptions.TlsCertificateStoreThumbprint)],
            ["TlsCertificatePassword"] = [nameof(ServerOptions.TlsCertificatePassword)]
        };

        private static readonly Dictionary<string, string[]> ImapSharedProjection = new()
        {
            ["Listeners"] = [nameof(ServerOptions.ImapPort), nameof(ServerOptions.ImapListeners)],
            ["BindAddress"] = [nameof(ServerOptions.BindAddress)],
            ["AllowRemoteConnections"] = [nameof(ServerOptions.AllowRemoteConnections)],
            ["DisableIPv6"] = [nameof(ServerOptions.DisableIPv6)],
            ["HostName"] = [nameof(ServerOptions.HostName)],
            ["TlsCertificate"] = [nameof(ServerOptions.TlsCertificate)],
            ["TlsCertificatePrivateKey"] = [nameof(ServerOptions.TlsCertificatePrivateKey)],
            ["TlsCertificateStoreThumbprint"] = [nameof(ServerOptions.TlsCertificateStoreThumbprint)],
            ["TlsCertificatePassword"] = [nameof(ServerOptions.TlsCertificatePassword)]
        };

        /// <summary>
        /// Options which describe individual SMTP listeners rather than the shared configuration.
        /// Changing one of these starts or stops a listener without touching the others.
        /// </summary>
        private static readonly string[] SmtpListenerSetOptionNames =
        [
            nameof(ServerOptions.Port),
            nameof(ServerOptions.TlsMode),
            nameof(ServerOptions.SmtpListeners)
        ];

        /// <summary>
        /// Explicit replacement values for options whose "make it different" value cannot be
        /// derived generically without accidentally resolving to the same listener set.
        /// </summary>
        private static readonly Dictionary<string, object> ExplicitDifferentValues = new()
        {
            [nameof(ServerOptions.SmtpListeners)] = new[] { new SmtpListenerOptions { Port = 2525, TlsMode = TlsMode.StartTls } },
            [nameof(ServerOptions.ImapListeners)] = new[] { new ImapListenerOptions { Port = 993, TlsMode = TlsMode.ImplicitTls } }
        };

        private static string[] SmtpSharedOptionNames => SmtpSharedProjection.Values.SelectMany(names => names).Distinct().ToArray();

        private static string[] ImapSharedOptionNames => ImapSharedProjection.Values.SelectMany(names => names).Distinct().ToArray();

        public static IEnumerable<object[]> SmtpSharedOptions() =>
            SmtpSharedOptionNames.Select(name => new object[] { name });

        public static IEnumerable<object[]> SmtpListenerSetOptions() =>
            SmtpListenerSetOptionNames.Select(name => new object[] { name });

        public static IEnumerable<object[]> NonSmtpOptions() =>
            AllOptionNames()
                .Where(name => !SmtpSharedOptionNames.Contains(name) && !SmtpListenerSetOptionNames.Contains(name))
                .Select(name => new object[] { name });

        public static IEnumerable<object[]> ImapSharedOptions() =>
            ImapSharedOptionNames.Select(name => new object[] { name });

        public static IEnumerable<object[]> NonImapOptions() =>
            AllOptionNames()
                .Where(name => !ImapSharedOptionNames.Contains(name))
                .Select(name => new object[] { name });

        [Fact]
        public void SmtpListenerConfig_CoversExactlyTheSharedOptions()
        {
            AssertProjectionCovers(typeof(SmtpListenerConfig), SmtpSharedProjection);
        }

        [Fact]
        public void ImapListenerConfig_CoversExactlyTheOptionsReadWhenTheListenerIsCreated()
        {
            AssertProjectionCovers(typeof(ImapListenerConfig), ImapSharedProjection);
        }

        [Fact]
        public void EveryServerOption_IsClassified()
        {
            //Every option is either shared by the SMTP listeners, part of the SMTP listener set,
            //part of the IMAP listener configuration, or read at the point of use. The last group
            //is what is left over, and this test only exists to make the classification a
            //deliberate act: if this fails because a new option appeared, decide which group it is
            //in rather than adding it to this list.
            string[] readAtPointOfUse =
            [
                nameof(ServerOptions.Urls),
                nameof(ServerOptions.Database),
                nameof(ServerOptions.NumberOfMessagesToKeep),
                nameof(ServerOptions.NumberOfSessionsToKeep),
                nameof(ServerOptions.BasePath),
                nameof(ServerOptions.Pop3TlsMode),
                nameof(ServerOptions.Pop3Port),
                nameof(ServerOptions.Pop3SecureConnectionRequired),
                nameof(ServerOptions.RecreateDb),
                nameof(ServerOptions.LockSettings),
                nameof(ServerOptions.DisableMessageSanitisation),
                nameof(ServerOptions.CredentialsValidationExpression),
                nameof(ServerOptions.SecureConnectionRequired),
                nameof(ServerOptions.SmtpAllowAnyCredentials),
                nameof(ServerOptions.RecipientValidationExpression),
                nameof(ServerOptions.MessageValidationExpression),
                nameof(ServerOptions.CommandValidationExpression),
                nameof(ServerOptions.Users),
                nameof(ServerOptions.WebAuthenticationRequired),
                nameof(ServerOptions.Mailboxes),
                nameof(ServerOptions.HtmlValidateConfig),
                nameof(ServerOptions.DisableHtmlValidation),
                nameof(ServerOptions.DisableHtmlCompatibilityCheck),
                nameof(ServerOptions.ValidateBareLineFeed),
                nameof(ServerOptions.DeliverToStdout),
                nameof(ServerOptions.ExitAfterMessages),
                nameof(ServerOptions.OAuth2Authority),
                nameof(ServerOptions.OAuth2Audience),
                nameof(ServerOptions.OAuth2Issuer),
                "DeliverMessagesToUsersDefaultMailbox"
            ];

            string[] classified = SmtpSharedOptionNames
                .Concat(SmtpListenerSetOptionNames)
                .Concat(ImapSharedOptionNames)
                .Concat(readAtPointOfUse)
                .Distinct()
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray();

            Assert.Equal(AllOptionNames().OrderBy(name => name, StringComparer.Ordinal).ToArray(), classified);
        }

        [Theory]
        [MemberData(nameof(SmtpSharedOptions))]
        public void SmtpListenerConfig_IsNotEqual_WhenASharedOptionChanges(string optionName)
        {
            (ServerOptions before, ServerOptions after) = OptionsDifferingBy(optionName);

            Assert.NotEqual(SmtpListenerConfig.From(before), SmtpListenerConfig.From(after));
        }

        [Theory]
        [MemberData(nameof(SmtpListenerSetOptions))]
        public void ResolvedSmtpListeners_Change_WhenAListenerSetOptionChanges(string optionName)
        {
            (ServerOptions before, ServerOptions after) = OptionsDifferingBy(optionName);

            Assert.NotEqual(before.ResolveSmtpListeners(), after.ResolveSmtpListeners());
        }

        [Theory]
        [MemberData(nameof(SmtpListenerSetOptions))]
        public void SmtpListenerConfig_IsEqual_WhenOnlyTheListenerSetChanges(string optionName)
        {
            (ServerOptions before, ServerOptions after) = OptionsDifferingBy(optionName);

            //A listener set change must not look like a shared change, or the whole server restarts
            //and every established session is dropped just to add a port.
            Assert.Equal(SmtpListenerConfig.From(before), SmtpListenerConfig.From(after));
        }

        [Theory]
        [MemberData(nameof(NonSmtpOptions))]
        public void SmtpListenerConfig_IsEqual_WhenAnUnrelatedOptionChanges(string optionName)
        {
            (ServerOptions before, ServerOptions after) = OptionsDifferingBy(optionName);

            Assert.Equal(SmtpListenerConfig.From(before), SmtpListenerConfig.From(after));
            Assert.Equal(before.ResolveSmtpListeners(), after.ResolveSmtpListeners());
        }

        [Theory]
        [MemberData(nameof(ImapSharedOptions))]
        public void ImapListenerConfig_IsNotEqual_WhenListenerOptionChanges(string optionName)
        {
            (ServerOptions before, ServerOptions after) = OptionsDifferingBy(optionName);

            Assert.NotEqual(ImapListenerConfig.From(before), ImapListenerConfig.From(after));
        }

        [Theory]
        [MemberData(nameof(NonImapOptions))]
        public void ImapListenerConfig_IsEqual_WhenOtherOptionChanges(string optionName)
        {
            (ServerOptions before, ServerOptions after) = OptionsDifferingBy(optionName);

            Assert.Equal(ImapListenerConfig.From(before), ImapListenerConfig.From(after));
        }

        private static void AssertProjectionCovers(Type projectionType, Dictionary<string, string[]> memberMap)
        {
            PropertyInfo[] projectedProperties = projectionType.GetProperties(BindingFlags.Public | BindingFlags.Instance);

            Assert.Equal(
                memberMap.Keys.OrderBy(name => name, StringComparer.Ordinal).ToArray(),
                projectedProperties.Select(p => p.Name).OrderBy(name => name, StringComparer.Ordinal).ToArray());

            foreach ((string member, string[] optionNames) in memberMap)
            {
                foreach (string optionName in optionNames)
                {
                    Assert.True(
                        typeof(ServerOptions).GetProperty(optionName) != null,
                        $"{projectionType.Name}.{member} claims to cover {optionName}, which is not a ServerOptions property");
                }

                //A member which stands for exactly one identically named option must carry that
                //option's type, so a type change upstream shows up here rather than being silently
                //truncated into the projection.
                if (optionNames.Length == 1 && optionNames[0] == member)
                {
                    Assert.Equal(
                        typeof(ServerOptions).GetProperty(member).PropertyType,
                        projectionType.GetProperty(member).PropertyType);
                }
            }
        }

        private static (ServerOptions Before, ServerOptions After) OptionsDifferingBy(string optionName)
        {
            ServerOptions before = new ServerOptions();
            ServerOptions after = before with { };

            PropertyInfo option = typeof(ServerOptions).GetProperty(optionName);

            object differentValue = ExplicitDifferentValues.TryGetValue(optionName, out object explicitValue)
                ? explicitValue
                : MakeDifferentValue(option.PropertyType, option.GetValue(before));

            option.SetValue(after, differentValue);

            return (before, after);
        }

        private static IEnumerable<string> AllOptionNames() =>
            typeof(ServerOptions).GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.CanRead && p.CanWrite)
                .Select(p => p.Name);

        private static object MakeDifferentValue(Type type, object currentValue)
        {
            Type valueType = Nullable.GetUnderlyingType(type) ?? type;

            if (valueType == typeof(string))
            {
                return Equals(currentValue, "a") ? "b" : "a";
            }

            if (valueType == typeof(bool))
            {
                return !(currentValue is bool currentBool && currentBool);
            }

            if (valueType.IsEnum)
            {
                return Enum.GetValues(valueType).Cast<object>().First(value => !Equals(value, currentValue));
            }

            if (valueType == typeof(int) || valueType == typeof(long) || valueType == typeof(short) || valueType == typeof(byte))
            {
                long currentNumber = currentValue == null ? 0 : Convert.ToInt64(currentValue);
                return Convert.ChangeType(currentNumber + 1, valueType);
            }

            if (valueType.IsArray)
            {
                Type elementType = valueType.GetElementType();
                int length = currentValue is Array currentArray ? currentArray.Length + 1 : 1;
                Array result = Array.CreateInstance(elementType, length);

                //Leave the slots null for element types which cannot be constructed here; a
                //different length is enough to make the array different.
                if (elementType.GetConstructor(Type.EmptyTypes) != null)
                {
                    for (int i = 0; i < length; i++)
                    {
                        result.SetValue(Activator.CreateInstance(elementType), i);
                    }
                }

                return result;
            }

            throw new NotSupportedException(
                $"This test does not know how to change a value of type {type}. Add support for it here, and " +
                "check whether the new option needs to be part of the listener configuration projections.");
        }
    }
}
