using Checkmk.App.ViewModels;
using Checkmk.Core.Models;
using FluentAssertions;
using Xunit;

namespace Checkmk.Core.Tests;

/// <summary>
/// Ein Host, der nicht UP ist, steht in der Liste als eine rote Host-Zeile —
/// seine Services (veraltet, solange er weg ist) verschwinden.
/// </summary>
public class HostDownRowTests
{
    private static ServiceStatus Svc(string host, string desc, int state = 0)
        => new() { HostName = host, Description = desc, State = state };

    [Fact]
    public void Down_host_replaces_its_services_with_one_host_row()
    {
        var services = new List<ServiceStatus>
        {
            Svc("19-SW05", "Check_MK HW/SW Inventory", 1),
            Svc("19-SW05", "Check_MK"),
            Svc("19-SW02", "Check_MK HW/SW Inventory", 1)
        };
        var hosts = new List<HostStatus>
        {
            new() { HostName = "19-SW05", State = 1, PluginOutput = "No IP packet received" },
            new() { HostName = "19-SW02", State = 0 }
        };

        var rows = StatusViewModel.BuildListRows(services, hosts);

        rows.Where(r => r.HostName == "19-SW05").Should().ContainSingle()
            .Which.Should().Match<ServiceStatus>(r =>
                r.IsHostRow && r.ServiceState == ServiceState.Critical
                && r.StateLabel == "Down" && r.PluginOutput == "No IP packet received");
        rows.Where(r => r.HostName == "19-SW02").Should().ContainSingle()
            .Which.IsHostRow.Should().BeFalse();
    }

    [Fact]
    public void Unreachable_counts_as_not_up_and_keeps_ack_and_downtime()
    {
        var hosts = new List<HostStatus>
        {
            new() { HostName = "behind", State = 2, Acknowledged = 1, ScheduledDowntimeDepth = 1 }
        };

        var row = StatusViewModel.BuildListRows([Svc("behind", "CPU")], hosts).Single();

        row.StateLabel.Should().Be("Unreachable");
        row.IsAcknowledged.Should().BeTrue();
        row.InDowntime.Should().BeTrue();
    }

    [Fact]
    public void All_hosts_up_returns_services_unchanged()
    {
        var services = new List<ServiceStatus> { Svc("a", "CPU") };

        StatusViewModel.BuildListRows(services, [new HostStatus { HostName = "a" }])
            .Should().BeSameAs(services);
    }

    [Fact]
    public void Host_status_reads_name_column()
    {
        var host = System.Text.Json.JsonSerializer.Deserialize<HostStatus>(
            """{"name":"19-SW05","alias":"Switch","state":1}""",
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));

        host!.HostName.Should().Be("19-SW05");
        host.Alias.Should().Be("Switch");
    }
}
