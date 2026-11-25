using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MCPanel.Tests;

public sealed partial class ReliabilityTests
{
    [DataTestMethod]
    [DataRow("127.0.0.1", true)]
    [DataRow("localhost", true)]
    [DataRow("::1", true)]
    [DataRow("[::1]", true)]
    [DataRow("", true)]
    [DataRow("0.0.0.0", false)]
    [DataRow("192.168.1.20", false)]
    public void AccountApiKnowsWhenItOnlyListensLocally(string address, bool loopback) =>
        Assert.AreEqual(loopback, AccountApiManagerService.IsLoopbackAddress(address));

    [TestMethod]
    public void RemoteAccessReservesTheAddressForThisUserAndOpensTheFirewallPort()
    {
        var script = AccountApiManagerService.BuildRemoteAccessScript(18088, "S-1-5-21-1-2-3-1001");

        // HTTP.sys: the wildcard prefix the listener uses for 0.0.0.0, for this user only.
        StringAssert.Contains(script, "$url = 'http://+:' + $port + '/'");
        StringAssert.Contains(script, "$port = 18088");
        StringAssert.Contains(script, "netsh.exe http delete urlacl url=$url");
        StringAssert.Contains(script, "netsh.exe http add urlacl url=$url 'sddl=D:(A;;GX;;;S-1-5-21-1-2-3-1001)'");
        StringAssert.Contains(script, "if ($LASTEXITCODE -ne 0) { throw", "A failed reservation must stop, not report success.");

        // Firewall: one rule by a fixed name, so a port change replaces it and full uninstall removes it.
        StringAssert.Contains(script, "Set-McPanelFirewallPort 'MCPanel Account API' 'TCP' $port");
        StringAssert.Contains(script, "function Set-McPanelFirewallPort");
        Assert.IsFalse(script.Contains("S-1-1-0"), "Never reserve the address for Everyone.");
    }

    [TestMethod]
    public void AccountApiPageOffersRemoteAccessAndLogsWhereItListens()
    {
        var page = ReadRepositoryFile("AccountApiPage.xaml");
        StringAssert.Contains(page, "Click=\"RemoteAccess_Click\"");
        StringAssert.Contains(page, "Content=\"{Binding RemoteAccessActionText}\"");

        var code = ReadRepositoryFile("AccountApiPage.xaml.cs");
        StringAssert.Contains(code, "EndpointText += \"（仅本机）\"", "A local-only listener must say so on the page.");
        StringAssert.Contains(code, "_service.SetRemoteAccessAsync(allow)");

        var host = ReadRepositoryFile("EmbeddedAccountApi/AccountApiHost.cs");
        StringAssert.Contains(host, "ServiceLog.Write(\"信息\", \"start\"", "Every start records the address it really listens on.");
        StringAssert.Contains(host, "（仅本机可访问）");
        StringAssert.Contains(host, "ServiceLog.Write(\"信息\", \"stop\"");

        var service = ReadRepositoryFile("AccountApiManagerService.cs");
        StringAssert.Contains(service, "await GrantRemoteAccessAsync(port, cancellationToken);",
            "Changing the port while remote access is on must re-grant the new port.");
    }
}
