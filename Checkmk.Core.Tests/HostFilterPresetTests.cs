using Checkmk.App.Models;
using Checkmk.App.Services;
using FluentAssertions;
using Xunit;

namespace Checkmk.Core.Tests;

/// <summary>
/// Der aus <c>viewer.json</c> vorgegebene Filter ist ein Startwert: auswaehlbar,
/// aenderbar — aber er darf nicht in die persoenliche Favoritenbibliothek
/// (<c>filter.json</c>) einsickern, sonst bliebe er dort stehen, nachdem der Admin
/// das Profil laengst geaendert hat.
/// </summary>
public class HostFilterPresetTests
{
    private const string TestSite = "TestSite";

    private sealed class FakeStore : IHostFilterStore
    {
        public HostFilterState State { get; init; } = new();
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

    /// <summary>Collection so, wie sie im Viewer-Modus entsteht.</summary>
    private static HostFilterCollection BuildViewer(FakeStore store)
        => new(store, new FakeSettingsStore(),
            new ViewerMode(new ViewerProfile()));

    // --- Viewer-Modus: filter.json bleibt komplett aussen vor ------------

    [Fact]
    public void Viewer_mode_does_not_load_the_personal_favorites()
    {
        var store = new FakeStore
        {
            State = new HostFilterState
            {
                Filters = [new HostFilter { Name = "Datenbanken" }, new HostFilter { Name = "CTX" }],
                ActiveFilterName = "Datenbanken"
            }
        };

        var collection = BuildViewer(store);

        collection.Filters.Should().BeEmpty();
        collection.Active.Should().BeNull();
    }

    /// <summary>
    /// Der gemeldete Fehler: Profil ohne Host-Bezug (<c>hostRegex: ""</c>), aber in
    /// der persoenlichen filter.json steht „Datenbanken" als zuletzt aktiv. Vorher
    /// gewann die filter.json und der Anwender sah nur die DB-Hosts.
    /// </summary>
    [Fact]
    public void Viewer_preset_without_host_scope_wins_over_the_persisted_selection()
    {
        var store = new FakeStore
        {
            State = new HostFilterState
            {
                Filters = [new HostFilter { Name = "Datenbanken", HostNameRegex = "^DB" }],
                ActiveFilterName = "Datenbanken"
            }
        };
        var collection = BuildViewer(store);

        collection.ApplyPreset(new ViewerView { FilterName = "Alles", HostRegex = "" }.ToHostFilter());

        collection.Filters.Should().ContainSingle().Which.Name.Should().Be("Alles");
        collection.Active!.Name.Should().Be("Alles");
        collection.Active.Matches("DBSQL01").Should().BeTrue();
        collection.Active.Matches("CTX-FARM-07").Should().BeTrue();
        collection.Active.ToLivestatus().Should().BeNull("kein Regex => serverseitig ungefiltert");
    }

    [Fact]
    public void Viewer_mode_never_writes_the_personal_filter_file()
    {
        var store = new FakeStore();
        var collection = BuildViewer(store);
        collection.ApplyPreset(new ViewerView { FilterName = "Alles" }.ToHostFilter());

        collection.Add(new HostFilter { Name = "Spontan" });
        collection.Active = collection.Filters.First(f => f.Name == "Spontan");
        collection.Update();

        store.LastSaved.Should().BeNull();
    }

    // --- ViewerView.ToHostFilter ----------------------------------------

    [Fact]
    public void Empty_host_regex_becomes_a_match_all_filter()
    {
        var filter = new ViewerView { FilterName = "Alles", HostRegex = "  " }.ToHostFilter();

        filter.HostNameRegex.Should().BeNull();
        filter.ExplicitHosts.Should().BeEmpty();
        filter.Matches("irgendwas").Should().BeTrue();
    }

    [Fact]
    public void Missing_filter_name_falls_back_to_Vorgabe()
        => new ViewerView { FilterName = "" }.ToHostFilter().Name.Should().Be("Vorgabe");

    [Fact]
    public void Include_hosts_are_trimmed_and_blanks_dropped()
    {
        var filter = new ViewerView { IncludeHosts = [" DBSQL01 ", "", "  ", "CTX07"] }.ToHostFilter();

        filter.ExplicitHosts.Should().Equal("DBSQL01", "CTX07");
    }

    [Fact]
    public void Host_regex_is_applied_when_set()
    {
        var filter = new ViewerView { HostRegex = "^CTX" }.ToHostFilter();

        filter.Matches("CTX07").Should().BeTrue();
        filter.Matches("DBSQL01").Should().BeFalse();
    }

    [Fact]
    public void Preset_is_activated_and_listed_first()
    {
        var store = new FakeStore
        {
            State = new HostFilterState { Filters = [new HostFilter { Name = "Eigener" }] }
        };
        var collection = Build(store);

        collection.ApplyPreset(new HostFilter { Name = "DB-Server", HostNameRegex = ".*sql.*" });

        collection.Filters[0].Name.Should().Be("DB-Server");
        collection.Active!.Name.Should().Be("DB-Server");
        collection.Filters.Should().Contain(f => f.Name == "Eigener");
    }

    [Fact]
    public void Applying_a_preset_does_not_persist()
    {
        // Seeded: die beiden Start-Filter sind hier nicht das Thema, und ihr
        // Anlegen wuerde selbst schon einen Schreibvorgang ausloesen.
        var store = new FakeStore { State = new HostFilterState { Seeded = true } };
        var collection = Build(store);

        collection.ApplyPreset(new HostFilter { Name = "Vorgabe", HostNameRegex = "web.*" });

        store.LastSaved.Should().BeNull();
    }

    [Fact]
    public void Preset_stays_out_of_the_file_when_the_user_saves_a_favorite_later()
    {
        var store = new FakeStore { State = new HostFilterState { Seeded = true } };
        var collection = Build(store);
        collection.ApplyPreset(new HostFilter { Name = "Vorgabe", HostNameRegex = "web.*" });

        collection.Add(new HostFilter { Name = "Meine DBs", ExplicitHosts = ["DBSQL01"] });

        store.LastSaved!.Filters.Should().ContainSingle().Which.Name.Should().Be("Meine DBs");
    }

    /// <summary>Solange die Vorgabe aktiv ist, darf kein Filtername als „zuletzt
    /// aktiv" gespeichert werden — sonst waere beim naechsten Start ein Filter
    /// vorgewaehlt, den es in der Datei gar nicht gibt.</summary>
    [Fact]
    public void Active_preset_is_not_written_as_the_remembered_selection()
    {
        var store = new FakeStore { State = new HostFilterState { Seeded = true } };
        var collection = Build(store);
        collection.ApplyPreset(new HostFilter { Name = "Vorgabe" });

        collection.Add(new HostFilter { Name = "Meine DBs" });

        store.LastSaved!.ActiveFilterName.Should().BeNull();
    }

    [Fact]
    public void Switching_away_from_the_preset_persists_the_users_choice()
    {
        var store = new FakeStore
        {
            State = new HostFilterState { Filters = [new HostFilter { Name = "Eigener" }] }
        };
        var collection = Build(store);
        collection.ApplyPreset(new HostFilter { Name = "Vorgabe" });

        collection.Active = collection.Filters.First(f => f.Name == "Eigener");

        store.LastSaved!.ActiveFilterName.Should().Be("Eigener");
        store.LastSaved!.Filters.Should().NotContain(f => f.Name == "Vorgabe");
    }

    [Fact]
    public void Preset_replaces_an_equally_named_favorite_instead_of_duplicating_it()
    {
        var store = new FakeStore
        {
            State = new HostFilterState
            {
                Filters = [new HostFilter { Name = "DB-Server", HostNameRegex = "alt" }]
            }
        };
        var collection = Build(store);

        collection.ApplyPreset(new HostFilter { Name = "db-server", HostNameRegex = "neu" });

        collection.Filters.Should().ContainSingle();
        collection.Filters[0].HostNameRegex.Should().Be("neu");
    }
}
