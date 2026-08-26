using Checkmk.App.Models;
using Checkmk.App.Services;
using FluentAssertions;
using Xunit;

namespace Checkmk.Core.Tests;

/// <summary>
/// Auf einem frischen Rechner stehen zwei Filter schon da: „Alle" und einer,
/// der wie der Anmeldename heisst („OsteL", „PeaterC") und gegen den
/// Host-Alias geht — letzterer aktiv.
///
/// Der Grund ist der Erstkontakt: Wer das Cockpit zum ersten Mal startet, sieht
/// sonst alle Checks der Stadt und muss sich erst einen Filter bauen, um seine
/// eigenen Geräte zu finden.
/// </summary>
public class FilterSeedTests
{
    private const string TestSite = "TestSite";

    private sealed class FakeStore : IHostFilterStore
    {
        public HostFilterState State { get; set; } = new();
        public HostFilterState? LastSaved { get; private set; }
        public string FilePath => "(memory)";
        public HostFilterState Load(string site) => State;
        public void Save(string site, HostFilterState state) => LastSaved = state;
    }

    private sealed class FakeSettingsStore : IConnectionSettingsStore
    {
        public ConnectionSettings Settings { get; } = new() { Site = TestSite };
        public string SettingsFilePath => "(memory)";
        public ConnectionSettings Load() => Settings;
        public string? LoadSecret(ConnectionSettings settings) => null;
        public void Save(ConnectionSettings settings, string plainSecret) { }
        public bool IsConfigured(ConnectionSettings settings) => true;
        public void UpdateActiveSite(string newSite) => Settings.Site = newSite;
    }

    private static HostFilterCollection Build(FakeStore store)
        => new(store, new FakeSettingsStore(), new ViewerMode(null));

    [Fact]
    public void A_fresh_installation_gets_two_filters()
    {
        var collection = Build(new FakeStore());

        collection.Filters.Select(f => f.Name).Should().Equal(
            HostFilterCollection.AllHostsFilterName,
            HostFilterCollection.MyDevicesFilterNameFor(Environment.UserName));
    }

    [Fact]
    public void The_personal_filter_is_preselected_and_matches_on_the_alias()
    {
        var collection = Build(new FakeStore());

        var mine = collection.Active!;
        mine.Name.Should().Be(HostFilterCollection.MyDevicesFilterNameFor(Environment.UserName));
        mine.Target.Should().Be(FilterTarget.Alias);
        mine.Matches("PC-4711", $"SchmidtT; {Environment.UserName}").Should().BeTrue();
        mine.Matches("PC-4712", "SchmidtT").Should().BeFalse();
    }

    /// <summary>
    /// Der persoenliche Filter heisst wie der Anmeldename — bei OsteL „OsteL",
    /// bei PeaterC „PeaterC". Derselbe Text steht im Regex und im Alias der
    /// Geraete; wer ihn im Dropdown sieht, muss nicht raten, wonach gefiltert
    /// wird.
    /// </summary>
    [Fact]
    public void The_personal_filter_is_named_after_the_login()
    {
        var mine = Build(new FakeStore()).Active!;

        mine.Name.Should().Be(Environment.UserName);
        mine.HostNameRegex.Should().Contain(Environment.UserName);
    }

    /// <summary>„Alle" ist wirklich alles — kein Regex, keine Liste, und
    /// serverseitig ungefiltert.</summary>
    [Fact]
    public void The_all_hosts_filter_filters_nothing()
    {
        var all = Build(new FakeStore()).Filters
            .First(f => f.Name == HostFilterCollection.AllHostsFilterName);

        all.Matches("irgendwas", null).Should().BeTrue();
        all.ToLivestatus().Should().BeNull();
    }

    /// <summary>
    /// Der Anmeldename wandert in einen regulaeren Ausdruck. Ein Punkt in
    /// „max.mustermann" waere sonst ein Platzhalter und der Filter zu weit.
    /// </summary>
    [Fact]
    public void The_login_name_is_escaped_for_the_regex()
        => Build(new FakeStore()).Active!.HostNameRegex
            .Should().Be(System.Text.RegularExpressions.Regex.Escape(Environment.UserName));

    [Fact]
    public void Seeding_is_remembered_so_it_happens_only_once()
    {
        var store = new FakeStore();
        Build(store);

        store.LastSaved!.Seeded.Should().BeTrue();

        // Naechster Start: der Anwender hat beide weggeraeumt.
        store.State = new HostFilterState { Seeded = true };
        Build(store).Filters.Should().BeEmpty(
            "wer die Start-Filter loescht, soll sie nicht beim naechsten Start wiederhaben");
    }

    [Fact]
    public void An_existing_installation_keeps_its_filters()
    {
        var store = new FakeStore
        {
            State = new HostFilterState
            {
                Filters = [new HostFilter { Name = "Datenbanken", HostNameRegex = "^db" }],
                ActiveFilterName = "Datenbanken"
            }
        };

        var collection = Build(store);

        collection.Filters.Should().ContainSingle().Which.Name.Should().Be("Datenbanken");
        collection.Active!.Name.Should().Be("Datenbanken");
        store.LastSaved!.Seeded.Should().BeTrue("auch Bestandsrechner saeen kein zweites Mal");
    }

    /// <summary>Im Viewer-Modus kommt der Filterzustand ausschliesslich aus
    /// <c>viewer.json</c> — dort darf nichts hinzugesaet werden.</summary>
    [Fact]
    public void Viewer_mode_gets_no_starter_filters()
    {
        var store = new FakeStore();
        var collection = new HostFilterCollection(store, new FakeSettingsStore(),
            new ViewerMode(new ViewerProfile()));

        collection.Filters.Should().BeEmpty();
        store.LastSaved.Should().BeNull();
    }
}
