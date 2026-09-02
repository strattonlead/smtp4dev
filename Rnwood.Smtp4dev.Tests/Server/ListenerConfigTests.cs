using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Rnwood.Smtp4dev.Server.Settings;
using Xunit;
using ImapListenerConfig = Rnwood.Smtp4dev.Server.ImapServer.ImapListenerConfig;
using SmtpListenerConfig = Rnwood.Smtp4dev.Server.Smtp4devServer.SmtpListenerConfig;

namespace Rnwood.Smtp4dev.Tests.Server
{
    /// <summary>
    /// The listener projections decide whether a settings change is allowed to tear down connections which are
    /// in progress. These tests pin down exactly which options each projection covers, so that adding a
    /// listener relevant option to <see cref="ServerOptions"/> without adding it to the projection fails here
    /// rather than silently never taking effect.
    /// </summary>
    public class ListenerConfigTests
    {
        private static readonly string[] SmtpListenerOptionNames =
        {
            nameof(ServerOptions.Port),
            nameof(ServerOptions.BindAddress),
            nameof(ServerOptions.AllowRemoteConnections),
            nameof(ServerOptions.DisableIPv6),
            nameof(ServerOptions.HostName),
            nameof(ServerOptions.TlsMode),
            nameof(ServerOptions.SslProtocols),
            nameof(ServerOptions.TlsCipherSuites),
            nameof(ServerOptions.MaxMessageSize),
            nameof(ServerOptions.AuthenticationRequired),
            nameof(ServerOptions.SmtpEnabledAuthTypesWhenNotSecureConnection),
            nameof(ServerOptions.SmtpEnabledAuthTypesWhenSecureConnection),
            nameof(ServerOptions.TlsCertificate),
            nameof(ServerOptions.TlsCertificatePrivateKey),
            nameof(ServerOptions.TlsCertificateStoreThumbprint),
            nameof(ServerOptions.TlsCertificatePassword)
        };

        private static readonly string[] ImapListenerOptionNames =
        {
            nameof(ServerOptions.ImapPort),
            nameof(ServerOptions.BindAddress),
            nameof(ServerOptions.AllowRemoteConnections),
            nameof(ServerOptions.DisableIPv6),
            nameof(ServerOptions.HostName)
        };

        public static IEnumerable<object[]> SmtpListenerOptions() =>
            SmtpListenerOptionNames.Select(name => new object[] { name });

        public static IEnumerable<object[]> NonSmtpListenerOptions() =>
            OtherOptionNames(SmtpListenerOptionNames).Select(name => new object[] { name });

        public static IEnumerable<object[]> ImapListenerOptions() =>
            ImapListenerOptionNames.Select(name => new object[] { name });

        public static IEnumerable<object[]> NonImapListenerOptions() =>
            OtherOptionNames(ImapListenerOptionNames).Select(name => new object[] { name });

        [Fact]
        public void SmtpListenerConfig_CoversExactlyTheOptionsReadWhenTheListenerIsCreated()
        {
            AssertProjectionCovers(typeof(SmtpListenerConfig), SmtpListenerOptionNames);
        }

        [Fact]
        public void ImapListenerConfig_CoversExactlyTheOptionsReadWhenTheListenerIsCreated()
        {
            AssertProjectionCovers(typeof(ImapListenerConfig), ImapListenerOptionNames);
        }

        [Theory]
        [MemberData(nameof(SmtpListenerOptions))]
        public void SmtpListenerConfig_IsNotEqual_WhenListenerOptionChanges(string optionName)
        {
            (ServerOptions before, ServerOptions after) = OptionsDifferingBy(optionName);

            Assert.NotEqual(SmtpListenerConfig.From(before), SmtpListenerConfig.From(after));
        }

        [Theory]
        [MemberData(nameof(NonSmtpListenerOptions))]
        public void SmtpListenerConfig_IsEqual_WhenOtherOptionChanges(string optionName)
        {
            (ServerOptions before, ServerOptions after) = OptionsDifferingBy(optionName);

            Assert.Equal(SmtpListenerConfig.From(before), SmtpListenerConfig.From(after));
        }

        [Theory]
        [MemberData(nameof(ImapListenerOptions))]
        public void ImapListenerConfig_IsNotEqual_WhenListenerOptionChanges(string optionName)
        {
            (ServerOptions before, ServerOptions after) = OptionsDifferingBy(optionName);

            Assert.NotEqual(ImapListenerConfig.From(before), ImapListenerConfig.From(after));
        }

        [Theory]
        [MemberData(nameof(NonImapListenerOptions))]
        public void ImapListenerConfig_IsEqual_WhenOtherOptionChanges(string optionName)
        {
            (ServerOptions before, ServerOptions after) = OptionsDifferingBy(optionName);

            Assert.Equal(ImapListenerConfig.From(before), ImapListenerConfig.From(after));
        }

        private static void AssertProjectionCovers(Type projectionType, string[] expectedOptionNames)
        {
            PropertyInfo[] projectedProperties = projectionType.GetProperties(BindingFlags.Public | BindingFlags.Instance);

            Assert.Equal(
                expectedOptionNames.OrderBy(name => name, StringComparer.Ordinal).ToArray(),
                projectedProperties.Select(p => p.Name).OrderBy(name => name, StringComparer.Ordinal).ToArray());

            foreach (PropertyInfo projectedProperty in projectedProperties)
            {
                PropertyInfo option = typeof(ServerOptions).GetProperty(projectedProperty.Name);

                Assert.True(option != null, $"{projectionType.Name}.{projectedProperty.Name} is not a ServerOptions property");
                Assert.Equal(option.PropertyType, projectedProperty.PropertyType);
            }
        }

        private static (ServerOptions Before, ServerOptions After) OptionsDifferingBy(string optionName)
        {
            ServerOptions before = new ServerOptions();
            ServerOptions after = before with { };

            PropertyInfo option = typeof(ServerOptions).GetProperty(optionName);
            option.SetValue(after, MakeDifferentValue(option.PropertyType, option.GetValue(before)));

            return (before, after);
        }

        private static IEnumerable<string> OtherOptionNames(string[] listenerOptionNames) =>
            typeof(ServerOptions).GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.CanRead && p.CanWrite)
                .Select(p => p.Name)
                .Where(name => !listenerOptionNames.Contains(name));

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
                int length = currentValue is Array currentArray ? currentArray.Length + 1 : 1;
                return Array.CreateInstance(valueType.GetElementType(), length);
            }

            throw new NotSupportedException(
                $"This test does not know how to change a value of type {type}. Add support for it here, and " +
                "check whether the new option needs to be part of the listener configuration projections.");
        }
    }
}
