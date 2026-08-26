using Checkmk.App.Models;
using Checkmk.App.Services;
using Checkmk.Data;
using FluentAssertions;
using Xunit;

namespace Checkmk.Core.Tests;

/// <summary>
/// Der Diff beim Speichern — und die Wache gegen einen datenvernichtenden
/// Fehler, der am 2026-08-25 real zugeschlagen hat: Der Filter „XMS"
/// verschwand aus der Datenbank in derselben Millisekunde, in der er
/// veröffentlicht wurde.
/// </summary>
public class FilterPersistTests
{
    /// <summary>Merkt sich, was gespeichert und was gelöscht wurde.</summary>
    private sealed class RecordingStore : IFilterStore
    {
        public List<SharedFilter> Saved { get; } = [];
        public List<int> Deleted { get; } = [];
        public List<SharedFilter> Rows { get; } = [];

        public Task<IReadOnlyList<SharedFilter>> LoadAsync(string site, string user,
            CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<SharedFilter>>(Rows);

        public Task<IReadOnlyList<SharedFilter>> LoadCatalogAsync(string site,
            CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<SharedFilter>>([.. Rows.Where(r => r.IsPublished)]);

        public Task<IReadOnlyList<int>> LoadSubscriptionsAsync(string user,
            CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<int>>([]);

        /// <summary>Die zuletzt gesetzte Abo-Menge — fuer den Abbestell-Weg.</summary>
        public List<int>? Subscriptions { get; private set; }

        public Task SetSubscriptionsAsync(string site, string user, IReadOnlyList<int> filterIds,
            CancellationToken ct = default)
        {
            Subscriptions = [.. filterIds];
            return Task.CompletedTask;
        }

        public Task<int> SaveAsync(SharedFilter filter, string changedBy,
            CancellationToken ct = default)
        {
            Saved.Add(filter);
            return Task.FromResult(filter.HostFilterId > 0 ? filter.HostFilterId : 99);
        }

        /// <summary>Abonnentenzahl, die <see cref="DeleteAsync"/> melden soll —
        /// damit sich die Wache „nicht loeschen, solange abonniert" pruefen laesst.</summary>
        public int SubscribersOnDelete { get; set; }

        public Task<int> DeleteAsync(int id, CancellationToken ct = default)
        {
            if (SubscribersOnDelete > 0) return Task.FromResult(SubscribersOnDelete);
            Deleted.Add(id);
            return Task.FromResult(0);
        }

        public Task<int> ImportLegacyIfEmptyAsync(string site, string user,
            IReadOnlyList<SharedFilter> fromFile, CancellationToken ct = default)
            => Task.FromResult(0);
    }

    private sealed class StubFachbereiche : IFachbereichStore
    {
        public FachbereichSnapshot Current { get; } =
            new([new FachbereichRow(1, "5424 IT-Basis-Dienste", null)], []);
        public Task RefreshAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<int> CreateAsync(string n, string? d, CancellationToken ct = default)
            => Task.FromResult(1);
        public Task RenameAsync(int i, string n, string? d, CancellationToken ct = default)
            => Task.CompletedTask;
        public Task<int> CountFiltersAsync(int i, CancellationToken ct = default)
            => Task.FromResult(0);
        public Task DeleteAsync(int i, CancellationToken ct = default) => Task.CompletedTask;
    }

    private const string User = "OsteL";

    private static (CentralFilterService Svc, RecordingStore Store) Build(
        params SharedFilter[] existing)
    {
        var store = new RecordingStore();
        store.Rows.AddRange(existing);
        var cache = Path.Combine(Path.GetTempPath(), "zz-persist-" + Guid.NewGuid().ToString("N"));
        return (new CentralFilterService(store, new StubFachbereiche(), cache, User), store);
    }

    private static SharedFilter Row(int id, string name, int? fachbereich = null)
        => new(id, fachbereich, User, "LHP", name, ".*", []);

    // --- Der Fehler, der wirklich passiert ist ---------------------------

    [Fact]
    public async Task Eine_unvollstaendige_Liste_loescht_nichts()
    {
        // Real passiert: Beim Neuaufbau der Filterliste ist die Collection
        // kurzzeitig unvollstaendig. Ein in diesem Moment ausgeloestes Speichern
        // schloss aus „steht nicht mehr drin" auf „loeschen" — und riss einen
        // unbeteiligten Filter aus der Datenbank.
        var (svc, store) = Build(Row(9, "Datenbanken"), Row(11, "XMS"));
        await svc.LoadAsync("LHP", [], TestContext.Current.CancellationToken);

        // Nur EIN Filter kommt an, der andere fehlt (Liste im Umbau).
        var partial = new List<HostFilter>
        {
            new() { Id = 9, Owner = User, Name = "Datenbanken", HostNameRegex = ".*" }
        };

        var err = await svc.PersistAsync(partial, deleted: null,
            TestContext.Current.CancellationToken);

        err.Should().BeNull();
        store.Deleted.Should().BeEmpty("nur ausdruecklich genannte Ids duerfen geloescht werden");
    }

    [Fact]
    public async Task Nur_ausdruecklich_genannte_Ids_werden_geloescht()
    {
        var (svc, store) = Build(Row(9, "Datenbanken"), Row(11, "XMS"));
        await svc.LoadAsync("LHP", [], TestContext.Current.CancellationToken);

        await svc.PersistAsync(
            [new HostFilter { Id = 9, Owner = User, Name = "Datenbanken", HostNameRegex = ".*" }],
            deleted: [11], TestContext.Current.CancellationToken);

        store.Deleted.Should().BeEquivalentTo([11]);
    }

    [Fact]
    public async Task Fremde_Filter_werden_nie_geloescht()
    {
        // Ein abonnierter Filter gehoert seinem Autor. Ihn aus der eigenen
        // Liste zu nehmen heisst abbestellen, nicht loeschen.
        var (svc, store) = Build(new SharedFilter(20, 1, "GuentherJ", "LHP", "Backup", ".*", []));
        await svc.LoadAsync("LHP", [], TestContext.Current.CancellationToken);

        await svc.PersistAsync([], deleted: [20], TestContext.Current.CancellationToken);

        store.Deleted.Should().BeEmpty();
    }

    [Fact]
    public async Task Fremde_Filter_werden_nie_geschrieben()
    {
        var (svc, store) = Build(new SharedFilter(20, 1, "GuentherJ", "LHP", "Backup", ".*", []));
        var loaded = await svc.LoadAsync("LHP", [], TestContext.Current.CancellationToken);

        // Der Fremde steht in meiner Liste (abonniert) und wird mitgereicht.
        await svc.PersistAsync(loaded, deleted: null, TestContext.Current.CancellationToken);

        store.Saved.Should().BeEmpty();
    }

    // --- Der Ausgangsstand darf nicht verstuemmelt werden ----------------

    [Fact]
    public async Task Ein_Speichern_mit_Teilliste_legt_beim_naechsten_Mal_nichts_doppelt_an()
    {
        // Folgefehler desselben Denkfehlers: Wurde der Ausgangsstand durch die
        // Teilliste ERSETZT, galt der fehlende Filter danach als unbekannt —
        // und waere beim naechsten Speichern als neuer Datensatz ein zweites
        // Mal angelegt worden.
        var (svc, store) = Build(Row(9, "Datenbanken"), Row(11, "XMS"));
        await svc.LoadAsync("LHP", [], TestContext.Current.CancellationToken);

        await svc.PersistAsync(
            [new HostFilter { Id = 9, Owner = User, Name = "Datenbanken", HostNameRegex = ".*" }],
            deleted: null, TestContext.Current.CancellationToken);

        store.Saved.Clear();

        // Jetzt wieder vollstaendig, XMS unveraendert.
        await svc.PersistAsync(
        [
            new HostFilter { Id = 9, Owner = User, Name = "Datenbanken", HostNameRegex = ".*" },
            new HostFilter { Id = 11, Owner = User, Name = "XMS", HostNameRegex = ".*" }
        ], deleted: null, TestContext.Current.CancellationToken);

        store.Saved.Should().BeEmpty("unveraenderte Filter werden nicht neu geschrieben");
    }

    [Fact]
    public async Task Ein_wirklich_geaenderter_Filter_wird_gespeichert()
    {
        var (svc, _) = Build(Row(9, "Datenbanken"));
        await svc.LoadAsync("LHP", [], TestContext.Current.CancellationToken);

        var (svc2, store2) = Build(Row(9, "Datenbanken"));
        await svc2.LoadAsync("LHP", [], TestContext.Current.CancellationToken);

        await svc2.PersistAsync(
            [new HostFilter { Id = 9, Owner = User, Name = "Datenbanken", HostNameRegex = "^db" }],
            deleted: null, TestContext.Current.CancellationToken);

        store2.Saved.Should().ContainSingle(s => s.HostNameRegex == "^db");
    }

    // --- Katalog-Filter: abbestellen statt loeschen ----------------------

    /// <summary>
    /// Ein veroeffentlichter Filter gehoert nicht mehr nur mir. Ihn aus meiner
    /// Auswahl zu nehmen darf ihn nicht aus dem Katalog reissen — auch dann
    /// nicht, wenn ich selbst der Autor bin.
    /// </summary>
    [Fact]
    public async Task Abbestellen_loescht_den_eigenen_Katalog_Filter_nicht()
    {
        var (svc, store) = Build(Row(9, "Datenbanken"), Row(11, "CTX", fachbereich: 1));
        await svc.LoadAsync("LHP", [], TestContext.Current.CancellationToken);

        var err = await svc.UnsubscribeAsync([11], TestContext.Current.CancellationToken);

        err.Should().BeNull();
        store.Deleted.Should().BeEmpty();
        store.Subscriptions.Should().BeEmpty("der einzige abonnierte Katalog-Filter war CTX");
        svc.Current.Should().ContainSingle().Which.Name.Should().Be("Datenbanken");
    }

    /// <summary>
    /// Die neue Abo-Menge entsteht aus dem geladenen Stand, nicht aus der
    /// Liste in der Oberflaeche — sonst wuerde eine kurzzeitig unvollstaendige
    /// Collection fremde Abos wegraeumen. Derselbe Grund wie beim Loeschen.
    /// </summary>
    [Fact]
    public async Task Abbestellen_laesst_die_uebrigen_Abos_stehen()
    {
        var (svc, store) = Build(
            Row(11, "CTX", fachbereich: 1),
            Row(12, "Backup", fachbereich: 1),
            new SharedFilter(13, 1, "GuentherJ", "LHP", "Netzwerk", ".*", []));
        await svc.LoadAsync("LHP", [], TestContext.Current.CancellationToken);

        await svc.UnsubscribeAsync([11], TestContext.Current.CancellationToken);

        store.Subscriptions.Should().BeEquivalentTo([12, 13]);
    }

    [Fact]
    public async Task Ein_Katalog_Filter_ohne_Abonnenten_wird_geloescht()
    {
        var (svc, store) = Build(Row(11, "CTX", fachbereich: 1));
        await svc.LoadAsync("LHP", [], TestContext.Current.CancellationToken);

        var err = await svc.DeleteFromCatalogAsync([11], TestContext.Current.CancellationToken);

        err.Should().BeNull();
        store.Deleted.Should().BeEquivalentTo([11]);
    }

    /// <summary>
    /// Zwischen Anzeige und Klick kann jemand abonniert haben. Dann sagt der
    /// Store nein, und das muss beim Anwender ankommen statt lautlos zu
    /// verpuffen.
    /// </summary>
    [Fact]
    public async Task Ein_abonnierter_Katalog_Filter_wird_nicht_geloescht()
    {
        var (svc, store) = Build(Row(11, "CTX", fachbereich: 1));
        await svc.LoadAsync("LHP", [], TestContext.Current.CancellationToken);
        store.SubscribersOnDelete = 3;

        var err = await svc.DeleteFromCatalogAsync([11], TestContext.Current.CancellationToken);

        err.Should().Contain("CTX").And.Contain("3");
        store.Deleted.Should().BeEmpty();
        svc.Current.Should().ContainSingle(f => f.Name == "CTX");
    }

    [Fact]
    public async Task Veroeffentlichen_aendert_den_Filter_und_loescht_ihn_nicht()
    {
        // Genau der Vorgang, bei dem XMS verschwand.
        var (svc, store) = Build(Row(11, "XMS"));
        await svc.LoadAsync("LHP", [], TestContext.Current.CancellationToken);

        await svc.PersistAsync(
            [new HostFilter { Id = 11, Owner = User, Name = "XMS", HostNameRegex = ".*", FachbereichId = 1 }],
            deleted: null, TestContext.Current.CancellationToken);

        store.Deleted.Should().BeEmpty();
        store.Saved.Should().ContainSingle(s => s.HostFilterId == 11 && s.FachbereichId == 1);
    }
}
