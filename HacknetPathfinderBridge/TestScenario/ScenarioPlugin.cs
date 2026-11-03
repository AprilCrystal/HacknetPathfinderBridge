using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx;
using BepInEx.Hacknet;
using Hacknet;
using Hacknet.Extensions;
using Pathfinder.Port;

namespace Hnpb.TestScenario;

[BepInPlugin("com.hnc.bridge.testscenario", "Bridge Test Scenario", "0.1.0")]
[BepInDependency(BridgePlugin.Guid)]
public sealed class ScenarioPlugin : HacknetPlugin, IBridgeAdapter
{
    private IDisposable? registration;
    public string Id => "hnpb.testscenario";
    public override bool Load() { registration = BridgeAdapters.Register(this); return true; }
    public override bool Unload() { registration?.Dispose(); return true; }
    public object Describe() => new { Operations = new[] { "setup" }, Scope = "Extension I only; adds configured node and missing player tools; never bypasses target security" };
    private static OS Require(object? value)
    {
        var os = value as OS ?? throw new InvalidOperationException("Enter the extension first.");
        if (!Settings.IsInExtensionMode || ExtensionLoader.ActiveExtensionInfo?.Name != "I")
            throw new InvalidOperationException("Test setup is restricted to extension I.");
        return os;
    }
    public object Invoke(object? value, string operation, Dictionary<string, object> arguments)
    {
        var os = Require(value);
        if (operation != "setup" || arguments.Count != 0) throw new ArgumentException("Only setup with empty arguments is supported.");
        if (!os.netMap.nodes.Any(c => c.idName == "hnpb_security_lab"))
            Computer.loadFromFile(Path.Combine(ExtensionLoader.ActiveExtensionInfo.GetFullFolderPath(), "Nodes", "BridgeSecurityLab.xml"));
        var comp = os.netMap.nodes.Single(c => c.idName == "hnpb_security_lab");
        os.netMap.discoverNode(comp);
        var bin = os.thisComputer.getFolderFromPath("bin", true);
        var tools = new Dictionary<string, string> {
            ["SSHCrack.exe"] = "#SSH_CRACK#", ["FTPBounce.exe"] = "#FTP_CRACK#",
            ["SMTPOverflow.exe"] = "#SMTP_CRACK#", ["WebServerWorm.exe"] = "#WEB_CRACK#"
        };
        foreach (var tool in tools)
            if (!bin.files.Any(f => f.name.Equals(tool.Key, StringComparison.OrdinalIgnoreCase)))
                bin.files.Add(new FileEntry(ComputerLoader.filter(tool.Value), tool.Key));
        os.write("[Bridge Lab] Node discovered: 198.18.0.90; four cracking tools installed. Use local shell for proxy overload.");
        return Observe(os);
    }
    public object Observe(object? value)
    {
        var os = Require(value);
        var comp = os.netMap.nodes.SingleOrDefault(c => c.idName == "hnpb_security_lab");
        return new { Installed = comp != null, Target = comp == null ? null : new {
            comp.idName, comp.ip, comp.traceTime, comp.portsNeededForCrack,
            Admin = comp.adminIP == os.thisComputer.ip,
            comp.hasProxy, comp.proxyActive, comp.proxyOverloadTicks,
            FirewallPresent = comp.firewall != null, FirewallSolved = comp.firewall?.solved,
            Ports = comp.GetPortStateDict().Select(p => new { Protocol = p.Key, p.Value.PortNumber, p.Value.Cracked }).ToArray()
        }, Tools = os.thisComputer.getFolderFromPath("bin").files.Select(f => f.name).ToArray() };
    }
}
