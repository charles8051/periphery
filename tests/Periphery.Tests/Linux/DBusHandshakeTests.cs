using Periphery.Linux.DBus.Core;

namespace Periphery.Tests.Linux;

/// <summary>The SASL handshake and the system bus address (ADR-0091 D2).</summary>
public class DBusHandshakeTests
{
    [Fact]
    public void Handshake_AnswersTheChallengeEmpty_ThenBegins()
    {
        var challenged = DBusAuth.Next(DBusAuthState.AwaitingChallenge, "DATA");
        Assert.Equal(new DBusAuthStep(DBusAuthState.AwaitingOk, "DATA\r\n"), challenged);

        var accepted = DBusAuth.Next(challenged.State, "OK 0123456789abcdef0123456789abcdef");
        Assert.Equal(new DBusAuthStep(DBusAuthState.Authenticated, "BEGIN\r\n"), accepted);
    }

    [Fact]
    public void Handshake_AcceptsOkWithoutAChallenge() =>
        Assert.Equal(DBusAuthState.Authenticated, DBusAuth.Next(DBusAuthState.AwaitingChallenge, "OK 0123").State);

    // The state is passed as its number because a public test method cannot take an internal type.
    [Theory]
    [InlineData((int)DBusAuthState.AwaitingChallenge, "REJECTED EXTERNAL")]
    [InlineData((int)DBusAuthState.AwaitingChallenge, "ERROR")]
    [InlineData((int)DBusAuthState.AwaitingOk, "REJECTED")]
    [InlineData((int)DBusAuthState.AwaitingOk, "DATA")]
    [InlineData((int)DBusAuthState.AwaitingChallenge, "DATAX")]
    public void Handshake_AnythingElse_IsRejected(int state, string line) =>
        Assert.Equal(new DBusAuthStep(DBusAuthState.Rejected, null), DBusAuth.Next((DBusAuthState)state, line));

    [Fact]
    public void Greeting_SendsTheCredentialsByteThenExternal() =>
        Assert.Equal("\0AUTH EXTERNAL\r\n", DBusAuth.Greeting);

    [Theory]
    [InlineData(null, DBusAddress.DefaultSystemBusPath)]
    [InlineData("", DBusAddress.DefaultSystemBusPath)]
    [InlineData("unix:path=/run/dbus/system_bus_socket", "/run/dbus/system_bus_socket")]
    [InlineData("unix:abstract=/tmp/x;unix:path=/tmp/bus", "/tmp/bus")]
    [InlineData("unix:guid=abc,path=/tmp/bus", "/tmp/bus")]
    [InlineData("unix:path=/tmp/a%20b", "/tmp/a b")]
    [InlineData("unix:path=/tmp/a%2", DBusAddress.DefaultSystemBusPath)]
    [InlineData("tcp:host=localhost,port=1234", DBusAddress.DefaultSystemBusPath)]
    public void SystemBusAddress_TakesTheFirstUnixPath(string? environment, string expected) =>
        Assert.Equal(expected, DBusAddress.SystemBusSocketPath(environment));
}
