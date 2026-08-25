# Checkmk Cockpit

Avalonia-12-Desktop-Tool, das die **täglichen Checkmk-Admin-Handgriffe entwirrt** — die
Aktionen, die das Webinterface tief in Menüs vergräbt, liegen hier flach an der Zeile, wo
man das Problem sieht. Ziel-Backend: **Checkmk 2.5.x Pro** über die **REST-API v1**.

**Bewusst Windows-only** (dokumentierte Ausnahme zur Cross-Platform-Regel des
kroste-avalonia-Skills): App-Target `net10.0-windows`, `WinExe`, nur `win-x64`,
**kein Linux-Build/AppImage**. Grund sind tragende, Windows-gebundene Features —
DPAPI-Secret-Storage, WinRM/PowerShell-basierte Client-Aktualisierung und der
Tray-Balloon per `Shell_NotifyIcon`-P/Invoke. Diese Entscheidung ist final und
soll nicht „nach Cross-Platform repariert" werden.

> Diese Datei wird von Copilot/Claude in VS Code als always-on-Kontext gelesen. Regeln sind
> bewusst kurz, begründet und mit Beispielen — nicht wiederholen, was Linter/`.editorconfig`
> ohnehin erzwingen.

---

## 1 · Build, Test, Run (immer zuerst)

```bash
dotnet build Checkmk.slnx -c Release          # muss 0 Warnings / 0 Errors sein
dotnet test  Checkmk.slnx                      # xunit.v3 + FluentAssertions v7
# Self-contained Single-File (bevorzugte Distribution, kein System-.NET nötig):
dotnet publish Checkmk.App/Checkmk.App.csproj -c Release -r win-x64 \
  --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
```

`TreatWarningsAsErrors=true` ist gesetzt — **jede** Warnung bricht den Build. Vor jedem Commit
muss `dotnet build -c Release` sauber durchlaufen.

## 2 · Entwicklung in VS Code

- **Extensions:** C# Dev Kit (`ms-dotnettools.csdevkit`) + `ms-dotnettools.csharp`. Avalonia:
  „Avalonia for VS Code" für XAML-Preview/IntelliSense.
- **Debuggen:** F5 nutzt `.vscode/launch.json` (Config „Checkmk.App (Debug)"), `preLaunchTask`
  ist `build`.
- **Tasks** (`.vscode/tasks.json`): `build`, `test`, `publish-win-x64`, `clean-hard`
  (löscht rekursiv alle `bin/`/`obj/`).
- **Bazzite:** `dotnet`/`code` laufen in der Distrobox `dotnet10` (Fedora, RPM-installiert,
  via `distrobox-export --app code`), `$HOME` ist zwischen Host und Container geteilt.

## 3 · Architektur

| Projekt | Zweck |
|---|---|
| `Checkmk.Core` | REST-API-Client (`CheckmkClient`), Modelle, Optionen. **UI-unabhängig**, keine Avalonia-Abhängigkeit. |
| `Checkmk.Data` | EF Core 10 auf die zentrale MSSQL-Datenbank `CheckMK_Copilot` (FOC-SQL01): globale Vorgaben, Host-Metadaten, Bereiche und den Filter-Katalog. UI-unabhängig; EF gehört **nicht** in `Checkmk.Core`, der bleibt reiner REST-Client. |
| `Checkmk.App` | Avalonia-UI: Tabs, Dialoge, DI-Bootstrap. |
| `Checkmk.Core.Tests` | xunit.v3 + FluentAssertions **v7** (v8 = kommerzielle Xceed-Lizenz, siehe §6). |

**Muster:** MVVM mit CommunityToolkit.Mvvm (Source Generators, `[ObservableProperty]`,
`[RelayCommand]`); manuelles DI via `ServiceCollection` in `Program.cs`; NLog (Secrets maskiert).
`CheckmkClient` ist bewusst frei von UI/DI, damit er wiederverwendbar bleibt.

**Laufzeit-Client:** Verbindung ist zur Laufzeit änderbar → `ICheckmkClientProvider` baut den
`CheckmkClient` aus den aktuellen Settings neu (statt statischem `IOptions`). Nach dem Speichern
der Settings `Configure(...)` aufrufen, nicht die App neu starten.

**Fenster:** alle Fenster erben von `Controls/ChromeWindow` (randlos,
`WindowDecorations.BorderOnly` + `ExtendClientAreaToDecorationsHint=true` +
`ExtendClientAreaTitleBarHeightHint=-1` + `CanResize=true` — alle vier Zeilen
Pflicht, sonst schluckt die OS-Caption-Zone Klicks/Drag). Die Titelleiste ist
das UserControl `Controls/TitleBar` — ein Fenster packt schlicht
`<controls:TitleBar Title="..." />` an den oberen Rand, keine inline
`Border`+`PointerPressed`-Konstruktion mehr. Die TitleBar setzt intern die
Avalonia-12-`chrome:WindowDecorationProperties.ElementRole`-Rollen
(`TitleBar` = nativer Drag/Doppelklick via HTCAPTION, `User` an Fensterbuttons
und Extras — Klicks laufen als HTCLIENT direkt zu den Controls). Für Extras in
der Titelleiste (z. B. Site-Umschalter im MainWindow) gibt es die
`TitleBar.Extras`-Property (ContentProperty), Kinder darin erben automatisch
die `User`-Rolle. **Zusaetzlich Pflicht:** der managed Drag-Fallback in
`TitleBar.OnBarPointerPressed` filtert per Visual-Tree-Walk
(`LandedOnInteractiveChild`) alle Klicks aus, die aus einem interaktiven Kind
gebubbelt kommen. Ohne diesen Guard startet `BeginMoveDrag` bei jedem Klick auf
die Site-ComboBox einen Fenster-Drag, der Pointer geht ans OS und das Dropdown
oeffnet nie (nur der ToolTip erscheint). Buttons sind davon nicht betroffen, weil
sie den Press selbst als handled markieren — die ComboBox tut das nicht. Der Guard
existierte schon einmal in `ChromeWindow` (be95724) und ging beim TitleBar-Refactor
(23160d8) verloren; **nicht wieder als „durch ElementRole ueberfluessig" entfernen**,
solange der Fallback-Handler dranhaengt. Palette/Buttons: `Kroste*Brush` + `Button.chrome` in
`App.axaml`. **App-Icon:** `Assets/app.ico` (`<ApplicationIcon>`, EXE) +
`Assets/app.png` (`ChromeWindow.Icon`, Fenster/Taskleiste; die TitleBar zeigt
es zusätzlich klein oben links). Dialoge mit Laufzeitdaten (z. B.
`ServiceActionDialog`) werden direkt instanziiert, nicht über DI. Referenz
für das gesamte Muster: kroste-avalonia-Skill (Klemmbrett-Scaffold).
**Version:** Anzeige immer über `AppVersion.Display` (MinVer-`InformationalVersion`, ohne
`+`-Suffix) — `Assembly.GetName().Version` liefert bei MinVer nur `Major.0.0.0`.

## 4 · Aktueller Funktionsstand

- **Status-Tab:** Host-/Service-Livestatus (Polling, Auto-Refresh), Ampel-Punkte,
  Freitext-Filter (Host/Service/**Ausgabe**/**Alias**), „Nur Probleme". **Ack + Downtime
  direkt aus der Liste** (Toolbar-Button + Rechtsklick): Zeile wählen → Dialog mit
  Pflicht-Kommentar; Downtime mit Dauer-Presets. **Bulk-Ack/Downtime**: Ctrl/Shift-Klick
  markiert mehrere Services; ein Kommentar für alle, iterative Ausführung mit Fortschritt
  „Ack 3/12: host/service" in der Statusleiste. Einzelfehler brechen den Bulk nicht ab,
  werden geloggt und am Ende summiert. Spalte **Age** (Zeit seit letzter Statusänderung)
  statt „Letzter Check". **CSV-Export** der gefilterten Ansicht via `CsvExporter`
  (Semikolon, UTF-8-BOM, RFC-4180-Quoting).
- **Refresh läuft im Hintergrund** (seit v1.8.0). Bei ungefiltertem Blick auf ~32.000
  Checks stand die App vorher mehrere Sekunden. Drei Ursachen, alle beseitigt — und
  alle drei sind leicht wieder einzubauen:
  1. **`CheckmkClient.GetAsync` streamt.** Vorher `ReadAsStringAsync` + synchrones
     `JsonSerializer.Deserialize` — der Parse lief nach dem `await` wieder auf dem
     UI-Thread. Jetzt `HttpCompletionOption.ResponseHeadersRead` +
     `DeserializeAsync` + durchgängiges `ConfigureAwait(false)`. Nicht auf den
     String-Weg zurückbauen: der puffert zweistellige Megabytes und blockiert.
     Preis: bei kaputter Antwort gibt es keinen vollen Body mehr für die
     Fehlermeldung — `CountingStream` schneidet dafür die ersten 2 KB mit
     (reicht, um Proxy-HTML statt JSON zu erkennen).
  2. **`BulkObservableCollection.ReplaceAll` statt `Clear()` + N × `Add()`.**
     32.000 Zeilen einzeln einzufügen sind 32.000 `CollectionChanged`-Zustellungen
     ans DataGrid; ein Reset kostet dank Zeilen-Virtualisierung fast nichts. Der
     Reset räumt allerdings die Grid-Selektion ab — `ApplyVisible` zieht sie über
     `ServiceKey` (Host + Description) nach, sonst verliert ein Auto-Refresh alle
     30 s die markierte Zeile.
  3. **Der Baum wird nur gebaut, wenn er sichtbar ist** (`BuildTreeIfVisible`,
     `_treeStale`). In der Tabellenansicht war das ein ViewModel je Host für nichts.
  Fortschritt: `CountingStream` meldet gedrosselt (alle 256 KB) Bytes, `RefreshSegment`
  bildet sie auf ein Segment des Balkens ab (Hosts 0–10 %, Services 10–80 %,
  Auswerten/Anzeigen bis 100 %). **Checkmk schickt die großen Livestatus-Antworten
  chunked, also ohne `Content-Length`** — Nenner ist deshalb die Antwortgröße des
  letzten Laufs aus `statusview.json` (`LastHostBytes`/`LastServiceBytes`); ohne
  Schätzer läuft der Balken indeterminate. Restzeit wird linear hochgerechnet.
  Ein neuer Refresh **bricht den laufenden ab** (`_refreshCts`), der Timer-Tick
  dagegen **verwirft sich selbst**, solange `IsBusy` — sonst käme bei 32.000 Checks
  und kurzem Intervall nie einer durch. `_refreshRun` verwirft verspätete
  Fortschrittsmeldungen abgebrochener Läufe, sonst verstellen sie den Balken des
  Nachfolgers.
- **Bereiche-Tab** (nur mit zentraler Datenbank; ohne sie wird der Tab in
  `MainWindow.axaml.cs` **entfernt**, nicht versteckt — ein Tab, der nur „nicht
  verfügbar" sagt, ist schlechter als keiner). Bereichsbaum mit Status-Rollup:
  Ampelpunkt = schlechtester Status der Hosts im Bereich *und darunter*.
  Anlegen/Umbenennen/Löschen, Unterbereiche, Zuweisung per Mehrfachauswahl
  (`HostMultiSelectDialog` mit Freitextfilter — 1105 Hosts einzeln zuzuweisen
  ist keine Option) sowie „Bereich zuweisen…" im Kontextmenü des Status-Tabs.
  Vier Punkte, die nicht wegoptimiert werden dürfen:
  1. **Die Host-Menge IST die Linse.** `AreaRollup.Compute` bekommt nur die
     Hosts, die auf den aktiven Filter passen; im Rollup wird **nicht** noch
     einmal gefiltert. Genau daher ist derselbe Serverraum für das DB-Team grün
     und für den Wachschutz rot.
  2. **Leer ≠ grün.** `AreaAggregate.HasHosts` unterscheidet „keine Hosts" von
     „alles in Ordnung"; das XAML zeichnet dafür einen grauen statt grünen Punkt.
     Sonst hält man 0 zugewiesene Hosts für ein gesundes System.
  3. **Der Baum wird nur neu gebaut, wenn sich der Bereichssatz ändert**
     (`RebuildIfChanged` über eine Signatur). Ein Neuaufbau bei jedem
     Status-Refresh klappt alle 30 s jeden aufgeklappten Ast zu.
  4. **Löschen nur, wenn leer.** `AreaStore.DeleteAsync` liefert die Zahlen
     zurück statt Unterbereiche mitzulöschen oder Hosts stillschweigend
     freizusetzen. `AreaRollup` schützt zusätzlich per `visited`-Satz gegen einen
     Zyklus im Baum — die Datenbank verhindert ihn nicht, ein `UPDATE` reicht.
  Der Sammelknoten **„Ohne Bereich"** ist kein Datensatz (`AreaId = -1`), sondern
  die Restmenge — die Arbeitsliste beim Zuordnen und der einzige Weg, einen
  vergessenen Host überhaupt zu bemerken.
- **Ein Bereich lässt sich aufklappen und zeigt seine Hosts.** Die Liste ist
  bewusst **ungefiltert**: Ampelpunkt und Zahl am Bereich zeigen die Linse des
  aktiven Filters, die Kindliste den tatsächlichen Bestand. Genau die Differenz
  war eine echte Verwirrung („der Container hat drei Geräte, warum steht da
  1?"). Hosts außerhalb des Filters stehen ausgegraut mit dem Vermerk „nicht im
  Filter" dabei, statt zu fehlen; weicht die Zahl ab, steht am Bereich zusätzlich
  „(3 zugeordnet)". Drei Punkte:
  1. **Die Kindliste wird nur bei echter Änderung neu gebaut**
     (`AreaNodeViewModel.SetHosts` vergleicht eine Signatur). Der Rollup läuft
     alle paar Sekunden; ein Neuaufbau bei jedem Durchlauf klappte jeden
     geöffneten Ast wieder zu — derselbe Grund wie bei `RebuildIfChanged`.
  2. **`TreeChildren` führt Unterbereiche und Hosts zusammen**, weil ein
     `TreeView` nur *eine* Kindliste binden kann. `Children` bleibt die
     Bereichshierarchie, `Flatten()` läuft weiter nur über Bereiche.
  3. **Der Baum bindet auf `SelectedTreeItem` (object), nicht auf
     `SelectedNode`.** Ein Klick auf einen Host bekäme sonst den falschen Typ.
     Die Bereichsauswahl bleibt bei einem Host-Klick stehen — wer einen Host
     unter „Container" anklickt, meint weiterhin den Container.
- **Bereiche je Site sichtbar** (Schema 4, Tabelle `AreaSite`). LHP und
  `Schul_IT` sind heute getrennt, sollen aber irgendwann zusammengeführt werden.
  Deshalb **keine Spalte `Site` auf `Area`**: Ein Standort ist ein Ort, kein
  Site-Eigentum — im Stadthaus kann Technik aus beiden Sites stehen. Die
  n:m-Zuordnung ist ein reiner **Sichtbarkeitsfilter** mit der Regel
  **keine Zeile = in allen Sites sichtbar**. Damit bleiben Bereiche aus der Zeit
  davor unverändert, und die Zusammenführung ist ein `DELETE FROM AreaSite`.
  `AreaStore` liest die Tabelle in einem **eigenen** try — fehlt sie, gelten
  die Bereiche überall, statt dass der ganze Refresh scheitert.

  Drei Dinge, die aus einem echten Fehlbild stammen (Schulen tauchten in der
  Schul-Sicht nicht auf, dafür die LHP-Bereiche):
  1. **Neue Bereiche bekommen die aktive Site** (`AreaViewModel.CreateAsync`).
     Ohne das gilt „keine Zeile = überall", und von Hand angelegte Bereiche wie
     „Container" standen mit in *jeder* Site. Die Regel „keine Zeile = überall"
     bleibt trotzdem richtig — sie ist der **Migrationsfall** für Bereiche aus
     der Zeit vor Schema 4, nicht die Vorgabe für neue.
  2. **Der Import hängt nicht mehr stillschweigend unter die Auswahl.**
     Vorher wurde `SelectedNode` als Elternteil genommen; die 82 Schulen
     landeten unter „Stadthaus". Da dieses keine Site-Zuordnung hat, war es in
     der Schul-Sicht sichtbar — und die Schulen darin *eingeklappt*, wirkten
     also verschwunden. Jetzt ist das eine Option im Dialog, standardmäßig aus.
  3. **Ein Kind mit unsichtbarem Elternteil steigt zur Wurzel auf**
     (`RebuildIfChanged`). Sonst wäre es in seiner eigenen Site unauffindbar.
     Verifiziert gegen FOC-SQL01.

  **`SiteSelectDialog` („Sichtbar in Sites…") ist Pflichtbestandteil**, nicht
  Komfort: Ohne ihn konnte der Store die Sichtbarkeit setzen, die Oberfläche
  aber nicht — die Korrektur ging nur über SQL.
- **Zuordnungsvorschläge: erst der Checkmk-Ortstag, dann das Namensmuster.**
  93 Bereiche, aber tausend Hosts von Hand zu verteilen ist keine Option.

  **Der Tag ist der Hauptweg** (Schema 6, `Area.HostTag`). Gemessen am
  2026-08-21 auf Site `schul_it`: 553 von 654 Hosts tragen
  `tag_location_school` mit Werten wie `schule_46`, 51 verschiedene Werte,
  keiner mehrdeutig. Das ist im Setup **gepflegt**, während das Namensmuster
  dieselbe Information nur aus dem Hostnamen *erschließt* — und dabei irrt:
  Der Regex ordnete `29-SW11` der Grundschule Bornim (11) zu, `PA04-1` dem
  Humboldt-Gymnasium (1) statt Helmholtz (4). **28 solcher Fehlzuordnungen**
  korrigiert der Tag, und er erfasst 85 Hosts zusätzlich, die aus der
  Namenskonvention fallen (`WLC-01SL-01`). Gespeichert wird der **Wert**, nicht
  der Schlüssel — welche Attribute gelten, steht in
  `GlobalSetting.HostLocationTagKeys` (`IHostLocationTags`, gefüllt beim
  Hosts-Refresh).

  **Das Muster bleibt trotzdem** (Schema 5, `Area.HostPattern`): Auf Site `LHP`
  gibt es praktisch keine Ortstags (`tag_location` auf 9 von 1438 Hosts), dort
  trägt der Hostname die Information — Schule 46 hat `46-SW04`, `46-USV`,
  `NAS46-01`, `PA46-01`, `ESX46-02`, `iRMC-46`. Es greift nur bei Hosts **ohne**
  passenden Tag.

  **Ein Tag, den kein Bereich beansprucht, schaltet das Muster *nicht* ab.**
  Naheliegend wäre die Gegenrichtung, sie ist aber falsch: Nicht jeder Tag ist
  eine Ortsidentität — `tag_location = aussen` auf LHP ist eine Kategorie. Ein
  zu weit greifendes Muster sieht man im Dialog („neu (Muster)") und wählt es
  ab; ein still fehlender Host fällt niemandem auf.

  **Der Nummernkreis gehört zum Schlüssel.** Beim Abgleich Tag→Bereich
  (`HostTagMatcher`) wird nie die nackte Zahl verglichen, sondern
  `präfix_zahl`. Der Bestand hat das sofort bestraft: Fünf Hosts der
  Karl-Foerster-Schule (`25-SW01`, `NAS25-01`, …) tragen
  `tag_location_filiale = filiale_04` und wären über die 4 beim
  Hermann-von-Helmholtz-Gymnasium gelandet, das die Schulnummer 4 hat.
  `HostTagMatcher.PrefixBySource` verbindet Importquelle und Nummernkreis;
  eine Quelle ohne Eintrag nimmt am Abgleich **nicht** teil.

  Der Abgleich selbst („Tags zuordnen…", `TagMatchDialog`) ist die eine Stelle,
  an der geraten werden **darf** — weil das Ergebnis vor Augen steht, bestätigt
  und danach als exakter Wert gespeichert wird. Die Übersetzung Nummer→Tag ist
  unregelmäßig (`schule_2526` für 25/26, aber `schule_10` für 10/30, `schule_01`
  für 1); eine Regel, die alle Fälle zur Laufzeit trifft, träfe irgendwann
  unbemerkt den falschen Bereich.

  Drei Punkte am Muster, die nicht vereinfacht werden dürfen:
  1. **Ziffern-Grenze im Muster** (`(?<!\d)46(?!\d)`). Ein simples „enthält 46"
     träfe auch `146-SW01` und `460-…`, und Schule 4 bekäme alle Hosts der
     Schulen 40–49.
  2. **Nur Vorschläge, nie automatisch.** Ein Muster kann danebenliegen, und
     tausend falsche Zuordnungen hinterher aufzuräumen ist teurer als einmal
     durchsehen. Vorausgewählt sind nur die **eindeutigen neuen**; Hosts, die
     schon woanders stehen oder auf mehrere Muster passen, bleiben eine bewusste
     Entscheidung.
  3. **Handgepflegte Muster überleben den Reimport.** `ExternalCode` steht
     neben `HostPattern`: Nur wenn sich der Code ändert oder noch kein Muster
     da ist, wird neu abgeleitet.
  Für Schulen kommt das Muster aus `SCHULNUM`: 45 tragen eine reine Nummer,
  vier sind **zusammengelegte** Schulen. Die stehen mit *zwei* Nummern in den
  Daten (`25/26`), im Betrieb wird aber nur **eine** benutzt — und welche, steht
  nirgends. `PotsdamPlaceImporter.CombinedSchoolNumbers` hält die Zuordnung
  (25/26→25, 10/30→30, 42/44→44, 36/45→36; Angabe des Fachbereichs,
  2026-08-21). Ein **unbekannter** Doppelcode bekommt bewusst **kein** Muster:
  Eine Alternative `(?:25|26)` zu bauen hieße, die Hosts einer Nummer zu
  beanspruchen, die vielleicht einer anderen Schule gehört — ein fehlendes
  Muster fällt dagegen daran auf, dass keine Vorschläge kommen.
  Die 33 ohne Nummer sind freie Träger und OSZ, also nicht städtisch und nicht
  im Monitoring. Für Verwaltungsstandorte stehen die Kürzel gar nicht in den
  offenen Daten — die trägt man einmal je Bereich ein.
  **Zwei Schulen fehlen den offenen Daten ganz:** `schule_61` (28 Hosts, der
  größte Standort überhaupt) und `schule_63` (10 Hosts) haben keinen Eintrag im
  Schulverzeichnis. Bereich anlegen und den Tag von Hand setzen —
  Rechtsklick → „Host-Zuordnung…".
  `HostPatternDialog` zeigt eine **Live-Vorschau der Treffer**: Ein Regex ist
  für die meisten unlesbar, „diese 7 Hosts würden zugeordnet" versteht jeder.
- **Technik ist verschiebbar** (`AreaStore.MoveHostsAsync`). Der Alltagsfall,
  den das Zuweisen einzelner Hosts nicht abdeckt: Ein Haus wird aufgelöst, die
  gesamte Technik wandert in den Container — und später vielleicht zurück.
  Im Kontextmenü des Bereichsbaums als „Technik verschieben nach…".
- **Bereiche sind Punkt *oder* Fläche** (Schema 3). Der Normalfall ist ein
  **Marker** — die meisten Standorte („Außenstelle X") sind auf einer Stadtkarte
  ein Punkt; eine Fläche lohnt nur, wo es auf den Umriss ankommt (Campus mit
  mehreren Serverräumen). Hat ein Bereich beides, gewinnt beim Zeichnen die
  Fläche, der Punkt bleibt Sprungziel. **Treffererkennung: Marker vor Fläche** —
  ein Marker ist wenige Pixel groß und liegt oft *in* einem größeren Bereich;
  gewänne die Fläche, wäre er nicht anklickbar.
- **Standort-Import** (`PotsdamPlaceImporter`) aus den **FeatureServern** von
  `geoportal.potsdam.de`. Drei Quellen, je eigener `ExternalSource`:

  | Quelle | Dienst | Anzahl | Adress-Dedup |
  |---|---|---|---|
  | Verwaltungsstandorte | `Verwaltung_LH_Potsdam` | 161 → 35 | **ja** |
  | Schulen | `Schulen` | 82 | nein |
  | Hochschulen | `Hochschulen` | 11 | nein |

  Die Schulen gehören zur Site `Schul_IT`. **Adress-Dedup nur bei der
  Verwaltung**: Dort sitzen bis zu einem Dutzend Dienststellen im selben Haus
  und sind *ein* Standort; bei Schulen ist jeder Eintrag eine eigene
  Einrichtung, auch wenn zwei sich ein Gelände teilen.

  Bewusst über die veröffentlichte REST-Schnittstelle und **nicht** direkt aus
  der Datenbank, obwohl die auf demselben FOC-SQL01 liegt: Ein Tabellenzugriff
  hinge an einem internen Schema, von dem der Fachbereich Vermessung nicht
  weiß, dass wir es lesen — bei einem Umbau dort wäre das Cockpit kaputt, ohne
  dass es jemand kommen sieht. Nicht „vereinfachen".

  Die Feldnamen unterscheiden sich je Dienst (`BEHOERDE` vs. `NAME`, `ADRESSE`
  vs. `STRASSE`) — deshalb Kandidatenlisten mit „erster Treffer gewinnt",
  dasselbe Muster wie bei `HostOsAttributeKeys`.

  Drei Regeln, die aus einem echten Fehlschlag stammen:
  1. **Namen müssen beim Import eindeutig gemacht werden**
     (`AreaStore.UniqueName`). Bereichsnamen sind je Ebene eindeutig (Index aus
     002), die amtlichen Listen halten sich nicht daran: „Musikschule" steht
     zweimal drin. Ohne Entschärfung scheitert der **komplette** Import an SQL
     2601, und der Anwender sieht nur „Import fehlgeschlagen". Entschärft wird
     über die Straße („Musikschule (Galileistraße 6)"), erst danach mit einer
     Nummer — die sagt einem Menschen nichts.
  2. **Abgleich über `ExternalSource`+`ExternalId`**, ein zweiter Lauf erzeugt
     keine Dubletten. Der Name wird dabei **nicht** überschrieben — wer
     „Stadthaus" statt der amtlichen Bezeichnung eingetragen hat, behält es.
  3. Namen auf 200 Zeichen kürzen — amtliche Schulnamen werden lang.
- **Schema-Warnung in der Statusleiste**: `CockpitDatabase.CheckAsync` läuft
  beim Start, ein Versionsunterschied erscheint als rotes Feld. Ohne das
  scheitert später irgendein Zugriff mit „Ungültiger Spaltenname" und niemand
  kommt darauf, dass nur ein Skript aus `db/` fehlt. Verifiziert: mit Schema 2
  und Programmstand 3 startet die App, warnt, und `AreaStore` behält seine alte
  Momentaufnahme statt zu werfen.
- **Karte im Bereiche-Tab** (`Controls/MapCanvas`): Kachelkarte mit
  Polygon-Overlay, Baum links, Karte rechts, Auswahl in beiden Richtungen
  verbunden. Flächen zeichnet man auf dem markierten Bereich (Punkte klicken,
  Doppelklick/Enter schließt, Rücktaste nimmt zurück, Esc bricht ab), gespeichert
  wird GeoJSON in `Area.GeometryJson`. Fünf Punkte, die Zeit gekostet haben:
  1. **WMS, nicht WMTS.** Das Matrix-Set `grid_3857` der LGB hat einen auf
     Brandenburg beschränkten Ursprung — globale Slippy-Map-Kachelindizes laufen
     dort in `TileOutOfRange`. Über `GetMap` gibt der Client die BBOX selbst vor,
     die `WebMercator` ohnehin ausrechnet; MapProxy liefert trotzdem aus seinem
     Kachel-Cache.
  2. **WMS 1.1.1 mit `SRS`, nicht 1.3.0 mit `CRS`.** In 1.3.0 hängt die
     Achsenreihenfolge vom Koordinatensystem ab; daran vertauschen sich Länge und
     Breite *lautlos*, und die Karte zeigt die falsche Weltgegend statt zu meckern.
  3. **Antwort auf Bilddaten prüfen** (`LooksLikeImage`). WMS meldet Fehler gern
     als XML mit Status 200 — ungeprüft landet die Fehlermeldung als „Kachel" im
     Plattencache und wird nie wieder neu geholt.
  4. **Schieben in Weltpixeln, nicht in Grad.** In Mercator sind Grad je nach
     Breite unterschiedlich breit; wer Grad addiert, dessen Karte rutscht unter
     dem Mauszeiger weg. Beim Zoomen bleibt der Punkt unter dem Zeiger stehen.
  5. **Treffererkennung: kleinste Fläche gewinnt.** Sonst verdeckt der Campus
     die Serverräume darin.
- **Flächen sind nachbearbeitbar** („Fläche bearbeiten", `MapCanvas.BeginEditing`).
  Bis dahin musste man eine Fläche neu zeichnen, wenn eine einzige Ecke daneben
  lag — bei einem Campus mit einem Dutzend Ecken dauert das länger als das
  erste Mal. Ecken ziehen, Kantenmitte klicken fügt ein, Rechtsklick oder Entf
  entfernt, Enter übernimmt, Esc verwirft. Vier Punkte:
  1. **Gearbeitet wird auf einer Kopie.** Esc muss zum unveränderten Ausgangs-
     zustand zurückführen, und der steht in `Shapes`.
  2. **Nie unter drei Ecken** (`MapGeometry.MinimumVertices`). Sonst bliebe eine
     Linie stehen, die als Fläche gespeichert würde — unsichtbar und nicht
     anklickbar. `RemoveVertex` gibt in dem Fall die Liste unverändert zurück.
  3. **Die Kantenmitte wird geografisch gebildet, nicht auf dem Bildschirm.**
     Sonst hinge das Ergebnis von der Zoomstufe ab.
  4. **Die Kante vom letzten zum ersten Punkt hat auch einen Griff** (Umlauf in
     `InsertMidpoint`) — genau dort fehlt beim Nachzeichnen am häufigsten eine
     Ecke.
- **Rechtsklick auf der Karte** öffnet dasselbe Menü wie am Baum (Hosts
  zuweisen, Technik verschieben, Host-Zuordnung, Fläche bearbeiten,
  Kartenhintergrund). Der Weg „Fläche sehen → im Baum suchen → Rechtsklick" war
  der Umweg bei jedem Zuordnen. **Ein Klick auf der Karte passt die Ansicht
  nicht neu ein** (`_selectionFromMap`): Man sieht die Fläche ja gerade, und
  beim Rechtsklick stünde das Menü sonst über einer anderen Stelle.
- **`Area.MapLayerKey` ist der Hintergrund je Bereich.** Auf der Campus-Ebene
  ist die Liegenschaftskarte brauchbar, auf der Stadtübersicht unlesbar —
  deshalb hängt die Wahl am Bereich, nicht an der Zoomstufe. Gespeichert wird
  der **Name** aus `MapLayers`, nicht die Adresse: Wechselt die Quelle, ist das
  ein `UPDATE` an einer Stelle statt an 93 Bereichen. Die Toolbar-Auswahl bleibt
  die persönliche Vorliebe und wird nur **übersteuert**, nicht überschrieben
  (`_userLayer`); ein Name, den es nicht mehr gibt, fällt auf die Vorgabe zurück.
  **Sechs Hintergründe umschaltbar** (`GlobalSetting.MapLayers`, alle einzeln
  gegen die Dienste verifiziert, Zoom 18 über dem Rathaus):

  | Name | Dienst | Layer | CRS |
  |---|---|---|---|
  | Luftbild | `mapproxy/dop20c` | `bebb_dop20c` | 3857 |
  | Stadtplan | `mapproxy/basemapde-bebb` | `basemapde_farbe` | 3857 |
  | Topographisch grau | `mapproxy/dtk10grau` | `bb_dtk10_grau` | 3857 |
  | Luftbild grau | `mapproxy/dop20g` | `bebb_dop20g` | 3857 |
  | Liegenschaftskarte | `ows/alkis_wms` | drei `adv_alkis_*` kombiniert | 3857 |
  | Stadtkarte Potsdam | `geoportal.potsdam.de` | `0..29` | **4326** |

  Nicht wegkürzen auf „nur Luftbild": Auf buntem Untergrund sind eingefärbte
  Flächen schwer zu lesen — die Graustufenkarte ist für den Ampelblick die
  bessere, die Stadtkarte 1:500 für die Gebäudeebene. Die Wahl liegt user-lokal
  in `statusview.json` (persönliche Ansichtsvorliebe, keine zentrale Vorgabe).

  Zwei Fallen dabei:
  - **ALKIS hat keine fertige Kartendarstellung.** `Farbe`/`SW`/`Gelb` sind
    *Stile*, keine Layer (liefern Fehler-XML); die Liegenschaftskarte entsteht
    erst aus der Kombination von `adv_alkis_tatsaechliche_nutzung`,
    `…_flurstuecke` und `…_gebaeude`.
  - **Nicht jeder Dienst kann Web-Mercator.** Der Kartenserver der
    Landeshauptstadt spricht nur EPSG:4326 und 25833 — deshalb hat
    `MapLayerDefinition` ein `Crs`-Feld, und `GeographicBbox` rechnet die
    Kachelgrenzen in Grad um. Über eine einzelne Kachel ist der Unterschied
    zwischen Mercator und Plattkarte vernachlässigbar; auf kleinen Zoomstufen
    wäre er es nicht, aber Gebäudekarten benutzt man ohnehin nur nah heran.

  Der Cache-Pfad trägt einen Hash aus URL+Layer, sonst zeigt die Karte nach dem
  Umstellen weiter das alte Bild; beim Umschalten wird zusätzlich der
  Speichercache geleert.
- **Kachel-Caching ist tragend, nicht Beiwerk.** Gemessen am 2026-08-21 gegen
  den LGB-Dienst: **eine kalte Kachel ~1,2 s, aus dem Cache ~0,7 ms — Faktor
  680.** Ein Bildschirm sind ~12 Kacheln, also 5 s für jeden neuen Ausschnitt
  und *jede* Zoomstufe. Drei Stufen, in dieser Reihenfolge gelesen:
  1. **Speicher** (`_memory`), 2. **lokale Platte**
  (`%LOCALAPPDATA%\Kroste\Checkmk\tiles`), 3. **gemeinsamer Speicher**
  (`GlobalSetting.MapTileSharePath`, z. B. Fileshare), erst dann der Dienst.
  Aus dem Netz Geholtes landet lokal *und* — wenn schreibbar — im gemeinsamen
  Speicher. Ohne den zahlen 48 Nutzer dieselbe Wartezeit und laden dieselben
  ~200 MB einzeln beim Landesdienst.
  **Fehlschläge beim Schreiben in den gemeinsamen Speicher werden nicht
  geloggt** — die meisten haben dort nur Leserecht, und das wäre eine Logzeile
  je Kachel.
- **Vorabladen** (`MapPrefetchPlanner` + `MapTileLoader.PrefetchAsync`):
  Stadtübersicht (z11–14) plus 3×3 Kacheln je Standort (z15–18). Bewusst
  **keine Flächendeckung** — Potsdam vollständig bis z18 wären >40.000 Kacheln
  *je Ebene*, mehrere GB. Gemessene Größenordnung: 35 Standorte → ~1000
  Kacheln (~10 min, ~100 MB), 117 Standorte → ~3300 (~33 min, ~320 MB).
  Eigener Semaphor (2) getrennt vom interaktiven (4): Hintergrundarbeit darf
  Schieben und Zoomen nie ausbremsen. Danach funktioniert die Standort-Sicht
  **auch ohne Internet**.
- **Auffrischen ist „stale-while-revalidate"**: Eine veraltete Kachel wird
  *sofort* angezeigt und nur im Hintergrund erneuert
  (`MapTileMaxAgeDays`, Vorgabe 180, 0 = nie). Niemand wartet je auf eine
  Auffrischung — Orthophotos werden jährlich beflogen, häufiger nachzuladen
  kostet Bandbreite ohne Gegenwert. **Der Quellenvermerk im Kartenbild ist
  Lizenzpflicht** (dl-de/by-2.0), nicht Zierde — nicht in ein Menü verschieben.
- **Spaltenkonfiguration (Status-Tab):** Der Spaltensatz der Service-Tabelle steht
  **nicht mehr im XAML**, sondern entsteht immer über `StatusColumnFactory` — einmal
  aus `columns.json` (Normalmodus, `StatusGridColumns.Merge/Apply/Capture`) und einmal
  aus `viewer.json` (Viewer-Modus, dort gesperrt). Zwei Quellen für denselben
  Spaltensatz wären zwangsläufig irgendwann uneinig; nicht wieder ins XAML zurückbauen.
  Bedienung: Rechtsklick auf die Kopfzeile → Checkbox-Liste, Drag am Kopf sortiert um.
  Drei Fallen, die schon zugeschnappt sind:
  1. **Breiten aus `Column.Width`, nicht `ActualWidth`.** Spalten, die rechts aus dem
     sichtbaren Bereich ragen, sind nicht gemessen und liefern Unsinn (20 px für eine
     110-px-Spalte) — gespeichert schrumpft die Tabelle bei jedem Start weiter.
     Stern-Breiten (Ausgabe-Spalte) werden als `null` gesichert, sonst frieren sie fest.
  2. **`ContextRequested` per Visual-Tree-Walk trennen** (`IsInsideColumnHeader`):
     Kopfzeile und Zellen liefern dasselbe Event am selben DataGrid, sonst bekommt man
     auf dem Header das Zeilen-Menü.
  3. **Neue Katalog-Spalten kommen ausgeblendet dazu** (`Merge`) — ein Update darf
     niemandem die gewohnte Ansicht umbauen. `DefaultLayout` ist exakt der alte
     XAML-Satz.
- **Baumansicht** (Umschalter Tabelle ⇄ Baum, im Status-Tab): Hosts als oberste Knoten mit
  **OS-Pictogramm** (`Assets/os/windows.png` bzw. Tux-Vektor, „?" bei unbekanntem OS),
  Ampelpunkt, Problem-Zähler; aufgeklappt die Services mit Ausgabe. OS-Familie wird aus
  der Check_MK-Agent-Ausgabe geparst (`OsDetection`) — kein Zusatzdienst nötig. Nur die
  **Familie** (Windows/Linux), die exakte Version bräuchte die HW/SW-Inventur
  (`os_version`). Kontextmenü im Baum ist knotenabhängig (Host vs. Service): Host-Details,
  Ack, Downtime, Kommentar, Client aktualisieren.
- **Tray & Notifications:** Minimieren legt die App ins **System-Tray** (nicht Taskleiste)
  und schaltet Auto-Refresh ein (`TrayController`). Tray-Icon zeigt per Ampelfarbe den
  schlechtesten Status im aktiven Filter, Tooltip mit Kurzfassung. `StatusChangeMonitor`
  vergleicht Snapshots, `IToastNotifier` meldet Änderungen und Recovery **gebündelt** —
  nur im aktiven Filter, keine Alarm-Sturm-Kaskade.
  WinRT-Toast über `Microsoft.Toolkit.Uwp.Notifications` (`ToastContentBuilder.Show`) —
  Action-Center-kompatibel. `ToastNotificationManagerCompat` registriert AumID +
  Startmenu-Shortcut + COM-Server; ein leerer `OnActivated`-Handler im
  `WindowsToastNotifier`-Ctor erzwingt die Registrierung sofort, statt sie lazy
  beim ersten `Show()`-Call laufen zu lassen. Nach jedem `Show()` wird
  `Notifier.Setting` geloggt — Windows sagt uns direkt, ob es blockt
  (Focus Assist, DisabledForApplication, GroupPolicy).
- **Hosts-Tab** (früher „Konfiguration"): Host-Liste mit Ordner/IP/Alias, „Änderungen aktivieren",
  **Service Discovery** (Toolbar-Button + Rechtsklick auf einer Zeile): startet
  `fix_all` als Hintergrund-Task auf dem Server, pollt bis `active=false`, aktiviert
  danach die Änderungen — bringt vorhandene Hosts wie `DBSQL01` ins Monitoring.
  Das „Host anlegen"-Formular ist per Default **ausgeblendet** (Setup-Handgriffe
  laufen zentral, Fehlbedienung produziert Config-Änderungen); wieder einblenden
  über `%APPDATA%\Kroste\Checkmk\bootstrap.json` mit `"showHostCreation": true`.
- **Host-Details** (`HostDetailWindow`): Doppelklick oder Rechtsklick auf eine Zeile
  öffnet ein eigenes Fenster mit Host-State (Ampel + **In-Wartung-** und
  **Acknowledged-Badge**), Config-Attributen (Ordner/IP/Alias), Plugin-Output,
  Service-Aggregat (OK/WARN/CRIT/UNK) und der Service-Tabelle. Ack + Downtime direkt
  auf einzelnen Services **und** auf dem kompletten Host („ganzer Host in Wartung" ist
  damit erledigt). Mehrere Detail-Fenster können parallel offen sein. **IP-Fallback**:
  wenn Checkmk keine IP liefert, ermittelt `IpResolver` sie via Ping/DNS und markiert
  die Herkunft im UI.
- **Kommentare**: bestehende Kommentare (Host + Service) werden im Host-Detail-Fenster
  unten aufgelistet (Zeitstempel absteigend). Neue Kommentare per „Host-Kommentar…" bzw.
  „Kommentar…" auf dem markierten Service; Status-Tab hat Rechtsklick → „Kommentar…".
  Persistent-Flag im Dialog wählbar. Delete-Endpoint noch nicht implementiert (2.4/2.5-API
  hat konkurrierende Varianten — nachziehen sobald an Live-Server verifiziert).
- **Client-Aktualisierung** ist seit v1.7.0 **ausgelagert** ins Plugin
  [`Checkmk-Plugin-AgentUpdater`](https://github.com/LHP542/Checkmk-Plugin-AgentUpdater).
  Wer die Funktion braucht, legt die Plugin-DLL in den `plugins/`-Ordner neben
  `Checkmk.App.exe`. Grund für das Auslagern: die Aktion braucht Admin-Credentials
  und ist nicht für jeden Cockpit-Nutzer gedacht. Das Plugin exportiert einen
  `IAgentUpdater`-Service (aus `Checkmk.PluginContracts.Services`), den andere
  Plugins konsumieren können (Plan: vSphere-Baseimage-Plugin für Batch-Updates).
- **Externe Plugin-Repos als Submodules**: unter `external-plugins/` liegen die
  Plugin-Repos als Git-Submodules. Nach `git submodule update --init --recursive`
  greift das `build/external-plugins.targets`-Target beim Cockpit-Debug-Build:
  jedes Plugin wird mitgebaut und die `CheckmkPlugin.*.dll` ins
  `Checkmk.App/bin/Debug/…/plugins/` kopiert — F5-Start hat die Plugins direkt
  drin. **CI/Release checken die Submodules bewusst NICHT aus** (`actions/checkout`
  ohne `submodules: true`), damit End-User-ZIPs plugin-frei bleiben — Plugins
  müssen aktiv installiert werden.
- **Autoupdater (Phase 1):** Beim Start fragt `GitHubReleasesUpdateChecker` den
  `Bootstrap.UpdateChannelUrl` ab (Default `api.github.com/repos/LHP542/Checkmk/releases/
  latest`), vergleicht mit `Assembly.Version` und meldet bei neuerer Version einen
  gelben Badge in der Statusleiste. Klick öffnet den `UpdateDialog` (Release-Notes +
  „Release-Seite öffnen"/„Später"/„Diese Version überspringen"). Skip-Version liegt in
  `%APPDATA%\Kroste\Checkmk\updates.json`.
  **Manuell (About-Box):** Button „Nach Updates suchen" ruft `CheckManuallyAsync`
  auf — ignoriert bewusst die übersprungene Version und gibt klares Feedback
  (aktuell / verfügbar → `UpdateDialog` / fehlgeschlagen). Gemeinsame Kernlogik mit
  dem Startup-Check über das private `EvaluateAsync(honorSkip)`.
  **Proxy-Fix (v1.2.1):** `HttpClient` nutzt `DefaultProxyCredentials`
  (Negotiate/NTLM über den angemeldeten Windows-User) — sonst 407 am FortiProxy.
- **Autoupdater Phase 2: Selbst-Ersetzen + Signatur.** `UpdateInstaller` lädt
  das ZIP, **prüft es**, entpackt daneben und startet eine `.bat`, die auf das
  Prozessende wartet, die Dateien ersetzt und neu startet — die laufende `.exe`
  kann sich unter Windows nicht selbst überschreiben.

  **Die Signaturprüfung ist gebaut und bewusst AUS** (`PublicKeyBase64` leer).
  Das ist eine Entscheidung, keine offene Aufgabe — bitte nicht „nachziehen".

  Sie war als Roadmap-Punkt 17 unter der Annahme notiert, ein Innentäter könne
  über `GlobalSetting.UpdateChannelUrl` allen ein Paket unterschieben. Technisch
  stimmt das (nachgemessen: `CheckMK_Copilot_Worker` hat `UPDATE` auf
  `GlobalSetting`, und der Verbindungsstring liegt entschlüsselbar neben jeder
  EXE) — **das Bedrohungsmodell trägt hier aber nicht:**

  - Alle 48 Nutzer sind **Systemadministratoren zentraler Dienste** — AD,
    Netzwerk, Dateidienste, Datenbanken. Wer von ihnen Code auf fremden
    Rechnern ausführen wollte, nähme Gruppenrichtlinien oder die
    Softwareverteilung, nicht den Update-Kanal eines Monitoring-Beiboots.
  - Schreibrecht auf dem Kanal-Ordner haben nur zwei Personen; die übrigen
    lesen. Der Vertrauensanker ist damit die **NTFS-Berechtigung**, und die ist
    mit Bordmitteln kontrollierbar.
  - Der Preis wäre dauerhaft: jedes Paket signieren, ein Schlüssel, der nie
    verlorengehen darf (sonst brauchen alle 48 ein von Hand verteiltes Binary),
    und eine Reihenfolge-Falle beim Ausrollen.

  Auch das naheliegende Ersatzargument trägt nicht: Ein halb kopiertes ZIP
  fängt die Prüfsumme nicht *nötigerweise* ab — ein abgeschnittenes Archiv hat
  kein gültiges Zentralverzeichnis, `ExtractToDirectory` wirft, und der
  Austausch startet gar nicht erst. Der Programmordner bleibt unangetastet.

  **Der Code bleibt trotzdem stehen**, weil er nichts kostet (ein `if` auf einen
  leeren String) und die Lage sich ändern kann — etwa wenn das Cockpit einmal
  über diesen Kreis hinaus verteilt wird oder der Kanal in ein weniger
  kontrolliertes Netz wandert. Dann genügt ein Schlüsselpaar.

  Sechs Punkte, die gelten, *falls* jemand sie einschaltet:
  1. **Der öffentliche Schlüssel steckt im Binary** (`UpdateSignature.PublicKeyBase64`),
     nicht in `GlobalSetting`. Läge er dort, könnte derselbe Zugriff, der die
     Adresse ändert, auch den Schlüssel austauschen.
  2. **Leerer Schlüssel = keine Prüfung**, damit bestehende Releases
     installierbar bleiben. Ab dem ersten eingetragenen Schlüssel ist ein
     gültiges Manifest **Pflicht**.
  3. **Kein Manifest ist ein Fehler, kein Durchlauf.** Wer den Download umlenken
     kann, kann auch das Manifest verschwinden lassen. Dasselbe gilt für ein
     nicht ladbares Manifest und eine kaputte Base64-Signatur — „konnte nicht
     prüfen" heißt nie „also durch".
  4. **Erst prüfen, dann entpacken.** ZIP-Entpacken ist selbst schon eine
     Angriffsfläche.
  5. **Signiert wird ein eigener, fester Text** (`UpdateManifest.SignedBytes`),
     nicht das JSON der Datei. Zwei Serialisierer schreiben Leerzeichen und
     Reihenfolge verschieden, und eine Zeilenendenormalisierung beim Kopieren
     würde jede Signatur ungültig machen.
  6. **Die Version gehört in die signierten Bytes.** Sonst ließe sich ein echt
     signiertes *altes* Paket als neues ausgeben und eine geschlossene Lücke
     zurückholen.
  Werkzeuge: `Checkmk.App.exe --make-update-key` erzeugt das Paar,
  `--sign-update <zip> <version> <privkey>` schreibt `update.json`. Der
  Release-Workflow signiert, wenn das Secret `UPDATE_SIGNING_KEY` gesetzt ist —
  die Prüfung darauf steht **im Skript**, nicht in einer `if`-Bedingung, weil
  der `secrets`-Kontext in Step-Bedingungen nicht verfügbar ist.
- **Der Update-Kanal darf ein Ordner sein** (`FileShareUpdateChecker`).
  In Betrieb seit 2026-08-21:
  `\\samba01\542$\5424_IT-Basis-Dienste\CheckMK\CheckMK_Copilot`.
  Erkannt wird das an der **Schreibweise** von `UpdateChannelUrl`
  (`\\`, `//` oder `X:`) und nicht an einer zweiten Einstellung — ein Pfad und
  eine URL sind nicht zu verwechseln, und ein Schalter mehr wäre einer, den
  jemand falsch setzt. Vier Punkte:
  1. **Die Version steht im Dateinamen** (`Checkmk-1.14.0-win-x64.zip`). Sie aus
     dem Paket zu lesen hieße, es bei jedem Start herunterzuladen und
     auszupacken — 88 MB, nur um festzustellen, dass sich nichts geändert hat.
  2. **Höchste Version gewinnt, nicht neuester Zeitstempel.** Ein
     zurückkopiertes älteres Paket ist die jüngste Datei und würde sonst als
     „Update" angeboten.
  3. **Liegt ein `update.json` da, gibt es den Ausschlag** — sonst könnte ein
     danebengelegtes ZIP das signierte überstimmen. Ist ein Signaturschlüssel
     hinterlegt, ist das Manifest **Pflicht**; ohne Manifest gibt es dann kein
     Update, statt eines ungeprüften.
  4. **Auch vom Share wird erst kopiert, dann entpackt.** Läge das Paket
     während des Vorgangs weiter auf dem Netzlaufwerk, könnte es zwischen
     Prüfung und Entpacken ausgetauscht werden — und ein wegbrechendes
     Netzlaufwerk hinterließe einen halb ersetzten Programmordner.
  Ein unerreichbarer Ordner ist `Debug`, nicht `Error`: Ein Notebook ohne
  Netzlaufwerk ist der Normalfall, nicht die Störung.
- **Host-Filter (beide Tabs):** Persistente Favoriten wählbar über eine ComboBox in der Tool-
  bar. Ein Favorit ist entweder ein **Hostname-Regex** (case-insensitive) oder eine explizite
  **Include-Liste** von Hostnamen. Aus dem Hosts-Tab lassen sich per Ctrl+Klick mehrere Hosts
  markieren und mit „Auswahl als Favorit…" als benannte Liste speichern. Verwaltung
  (Anlegen/Bearbeiten/Löschen/Aktivieren) im `FilterManagerWindow`.
  Anwendung ist rein clientside (bei ≤ ein paar tausend Hosts problemlos);
  Livestatus-Query-serverside kann später kommen, wenn nötig.
- **Filter-Katalog mit Abonnement** (Schema 7, `CentralFilterService`,
  `FilterStore`, `FachbereichStore`). Ohne Datenbank bleibt alles beim Alten:
  `HostFilterStore` auf `%APPDATA%\Kroste\Checkmk\filter.json`. Mit Datenbank
  liegen die Filter in `dbo.HostFilter`, und `filter.json` wird **einmalig**
  übernommen (`ImportLegacyIfEmptyAsync`).

  **Das Modell — und warum es Teams abgelöst hat.** Bis v1.11.0 gehörte ein
  Filter einem Team, und wer im Team war, sah ihn. Das setzt voraus, dass
  jemand Mitgliederlisten pflegt, und genau das ist nie passiert (0 Teams,
  0 Mitgliedschaften, gemessen 2026-08-22). Jetzt gilt:

  | | Teams (raus) | Katalog (drin) |
  |---|---|---|
  | Wer sieht einen Filter? | wer im Team ist | wer ihn **abonniert** |
  | Pflegeaufwand | Mitgliederlisten | keiner |
  | Fachbereich ist… | Zugriffsgrenze | **Ordnungsbegriff** |

  Sechs Punkte, die nicht wegvereinfacht werden dürfen:
  1. **Ein veröffentlichter Filter behält seinen Autor.** `OwnerUserName` ist
     *immer* gesetzt, auch im Katalog; `FachbereichId` sagt nur, ob und wo er
     veröffentlicht ist. Beim Team-Modell schlossen sich beide aus — ein
     geteilter Filter war herrenlos, und niemand konnte einen Tippfehler darin
     korrigieren. Ändern darf nur der Autor, alle anderen nur abonnieren.
  2. **Abonniert zählt nur, solange veröffentlicht.** Nimmt der Autor den
     Filter aus dem Katalog, verschwindet er bei den Abonnenten — sonst sähen
     Fremde weiter etwas, das nicht mehr geteilt ist. Die Abo-Zeile darf dabei
     stehenbleiben, sie greift einfach nicht.
  3. **Gelöscht wird nur, was ausdrücklich genannt ist** — nie das, was gerade
     nicht in der Collection steht. Diese Schlussfolgerung war ein
     datenvernichtender Fehler: Die Liste ist bei jedem Neuaufbau kurzzeitig
     unvollständig, und ein in diesem Moment ausgelöstes Speichern löschte
     einen völlig unbeteiligten Filter. Real am 2026-08-25: „XMS" verschwand in
     derselben Millisekunde, in der er veröffentlicht wurde — im Log als zwei
     `Filter-Save`-Zeilen mit 5 und dann 4 Filtern sichtbar. Deshalb führt
     `HostFilterCollection` eine eigene `_deleted`-Liste, `PersistAsync` läuft
     **serialisiert** statt Feuer-und-vergessen, und der Ausgangsstand wird
     fortgeschrieben statt ersetzt. Fremde Filter werden weder gelöscht noch
     geschrieben; ein abonnierter, den ich aus meiner Liste nehme, wird
     abbestellt.
  4. **Bei Ausfall wird nicht geschrieben** (`FilterOrigin.Cache`, `CanWrite`).
     Eine Änderung, die nur im Cache landet, wäre beim nächsten erfolgreichen
     Laden lautlos weg. Cache-Datei: `filter-cache.json`.
  5. **Der zuletzt aktive Filter bleibt lokal.** Persönliche Ansichtsvorliebe;
     zentral abgelegt würde der Wechsel des einen die Ansicht aller anderen
     umstellen.
  6. **Veröffentlichen darf jeder**, Fachbereiche verwalten die Admins
     (`AppAdmin`; leere Tabelle = jeder). Der Katalog ist Organisation, kein
     Zugriffsschutz — die echte Grenze bleibt die Checkmk-Rolle.

  **Es gibt bewusst keine „wer nichts abonniert, sieht alles"-Regel** (anders
  als beim Bereichsbaum). Dort ist die Alternative eine leere Karte; hier hat
  jeder seine eigenen Filter als Startpunkt, und ein Dropdown, das ungefragt
  mit allem volläuft, was 48 Leute je veröffentlicht haben, wäre schlechter
  als eine kurze Liste plus Katalog.

  **Fremdschlüssel gehören ins Modell**, auch ohne Navigations-Property
  (`HasOne<T>().WithMany().HasForeignKey(...)` in `CockpitDbContext`). Ohne sie
  kennt EF die Abhängigkeit nicht, darf den Elternsatz zuerst löschen, die
  Datenbank räumt per `ON DELETE CASCADE` die Kinder weg — und EFs eigenes
  DELETE trifft nichts mehr. Das meldet sich als
  `DbUpdateConcurrencyException („expected 1 row, affected 0")` und sieht wie
  ein Nebenläufigkeitsproblem aus, ist aber keines. Umgekehrt gilt: Was die
  Datenbank per Cascade räumt, darf der Store **nicht** zusätzlich löschen.
  Auf `FK_HostFilter_Fachbereich` liegt bewusst kein Cascade — einen
  Fachbereich zu löschen nimmt die Filter **nicht** mit, sondern gibt sie an
  ihre Autoren zurück (`FachbereichStore.DeleteAsync` nennt vorher die Zahl).
  Im **Viewer-Modus** bleibt die zentrale Quelle komplett draußen, genau wie
  `filter.json`: Der Filterzustand kommt dort ausschließlich aus `viewer.json`.
- **Viewer-Modus** (`viewer.json` **neben der Exe**, `ViewerProfile.LoadOrNull`):
  zweite Betriebsart für Leute, die nur gucken sollen. Liegt die Datei da, kommt die
  Verbindung aus ihr (`ViewerConnectionSettingsStore` statt `ConnectionSettingsStore`),
  der Spaltensatz der Service-Tabelle wird aus `columns` gebaut (`StatusColumnFactory`,
  Schlüssel = Checkmk-Sichtnamen wie `svc_state_age`) und `view` liefert Start-Filter.
  Lockdown: nur Status-Tab (Hosts/Dashboard werden in `MainWindow.axaml.cs` **entfernt**,
  nicht `IsVisible`-versteckt — sonst bleiben sie per Ctrl+Tab erreichbar), kein
  Einstellungen-Button, kein Ack/Downtime/Kommentar/Remote-Tool, keine Hotkeys,
  **keine Plugins** (`PluginLoader` wird übersprungen — sonst hebelt ein Plugin-Tab
  den Lockdown aus). Fehlt die Datei, ändert sich nichts; beide Modi laufen aus
  demselben Binary. Drei Punkte, die nicht „aufgeräumt" werden dürfen:
  1. **`secretBase64` ist Maskierung, keine Verschlüsselung** — nie als „Secret ist
     geschützt" dokumentieren. DPAPI ist user-gebunden, die Datei wird verteilt; AES mit
     Key im Binary wäre der SharedAes-Trick aus §8.20, den wir verworfen haben. Die echte
     Grenze ist die **Checkmk-Lese-Rolle** des Users im Profil. Die UI-Sperren sind
     Bedienkomfort, kein Zugriffsschutz — deshalb sitzen zusätzlich `CanWrite`-Guards in
     den ViewModels, nicht nur `IsVisible` im XAML. Base64 wird **strikt** als UTF-8
     dekodiert (`throwOnInvalidBytes`): der häufigste Bedienfehler ist Klartext im
     Base64-Feld, und wenn der zufällig gültiges Base64 ist, gäbe es sonst nur ein
     nichtssagendes `401 Wrong credentials` vom Server.
  2. **Kaputtes JSON schaltet den Viewer-Modus NICHT ab** — `LoadFrom` gibt dann ein
     Profil mit `LoadError` zurück. Ein Tippfehler darf keinem Nur-Gucker die volle
     Oberfläche freischalten.
  **Kiosk-Karte** (`map`-Abschnitt, Roadmap 28): `show: true` lässt den
  Bereiche-Tab im Viewer-Modus stehen, dazu `area` (Startbereich per **Name** —
  eine Id steht nirgends, wo ein Mensch sie ablesen könnte), `zoom`, `layer` und
  `tree` (Baum links; false = reine Kartenwand). Drei Punkte:
  1. **Ohne `show: true` bleibt der Tab weg.** Sonst bekäme jede bestehende
     Kiosk-Ausgabe beim Update ungefragt einen neuen Tab.
  2. **Lesend, ohne neue Sperren.** Sämtliche Schreibknöpfe der `AreaView`
     hängen schon an `CanWrite`; der Abschnitt schaltet nichts frei. Das
     Kontextmenü der Karte prüft zusätzlich in `OnMapAreaRightClicked`.
  3. **Startwerte, keine Sperre.** Wer vor dem Bildschirm steht, darf schieben
     und zoomen; nach dem Neustart steht wieder die vorgesehene Sicht da
     (`_viewerMapApplied` läuft genau einmal). Ein Bereichsname, den es nicht
     gibt, kommt ins Log statt in eine stumme Gesamtübersicht.
  Zusätzlich im Viewer-Modus: **`popUpOnProblem`** (Default true) holt bei einer
  Verschlechterung das Fenster maximiert nach vorn (`TrayController.PopUpForProblem`)
  und markiert die betroffene Zeile über `StatusViewModel.RequestSpotlight`. Nur bei
  `ChangeSummary.HasWorsened` — reine Recoveries dürfen nichts aufreißen — und nie bei
  aktivem Snooze. Der `Topmost`-Toggle in `PopUpForProblem` ist nötig, weil `Activate()`
  allein unter Windows den Vordergrund nicht erzwingt; den Tastaturfokus vergibt Windows
  trotzdem nach eigenen Regeln, sichtbar-und-oben ist garantiert, fokussiert nicht.
  3. **Der Filterzustand kommt ausschließlich aus dem Profil.** `HostFilterCollection`
     lädt im Viewer-Modus die persönliche `filter.json` gar nicht erst und persistiert
     nie; `StatusViewModel` ruft `ApplyPreset(v.ToHostFilter())` **bedingungslos** —
     auch bei leerem `hostRegex` (= alle Hosts). Ohne beides gewann die `filter.json`
     des Rechners, auf dem das Profil gebaut wurde: deren `ActiveFilterName` überstimmte
     die Vorgabe und die fremden Favoriten standen im Dropdown. Nicht auf „nur wenn ein
     Host-Bezug da ist" zurückbauen. `view`-Werte im Übrigen sind Startwerte und gehen
     nicht nach `statusview.json` (`PersistState` ist No-Op).
- **Settings:** Verbindung (Host/Site/User/Secret/HTTPS/Cert), Secret verschlüsselt
  via `WindowsDpapiProtector` (DPAPI-CurrentUser). Ablage user-lokal unter
  `%APPDATA%\Kroste\Checkmk\settings.json`. Zusätzlich `KnownSites: [...]` als
  Grundlage für den Site-Umschalter in der Titelleiste (z. B. `LHP-Prod` ⇄
  `Schul_IT` am selben Server — Host/User/Secret bleiben). Der Pfad ist per
  `bootstrap.json` (`SharedSettingsPath`) überschreibbar; alter Samba-Default aus
  v1.0-v1.4 wird beim nächsten Start automatisch auf den lokalen Default
  migriert. `hosts.json` (Domain-Zuordnung) bleibt zentral auf Samba01 —
  Metadaten, keine Secrets.

## 5 · Checkmk-REST-API — nicht-offensichtliche Regeln

Diese Punkte kosten sonst zuverlässig Zeit:

- **Pfad `v1`** (nicht `1.0`): `https://<host>/<site>/check_mk/api/v1/`. Site = URL-Segment
  hinter dem Host.
- **Bearer-Auth im Checkmk-Format:** `Authorization: Bearer <user> <secret>` — User und Secret
  durch **ein Leerzeichen** getrennt, *nicht* Base64. Falsches Format → `401 Wrong credentials`.
- **Automation-User + Automation-Secret** (nicht das GUI-Passwort). Seit 2.4/2.5 wird kein
  `automation`-User mehr auto-angelegt → eigenen anlegen, Rolle mind. für die genutzten Endpunkte.
- **`attributes` nie mit `null`-Werten senden.** Nicht gesetzte Attribute weglassen, sonst
  `400 "These fields have problems: attributes"`. Deshalb hat `JsonOpts` im Client
  `JsonIgnoreCondition.WhenWritingNull` — **nicht entfernen**.
- **Ordner = ID-Pfad, nicht Titel.** `folder` erwartet den ID-Pfad (`/datenbanken/db-mssql`)
  oder die 32-stellige Hex-ID; die Titel aus der Breadcrumb sind es *nicht*. ID steht in der
  Browser-URL hinter `folder=` bzw. via `folder_config`-Endpoint.
- **HTTP-Status ≠ fachlicher Erfolg.** Kommandos laufen serverseitig über Livestatus; bei
  Bedarf Zustand danach erneut abfragen. Discovery/Activate laufen als Hintergrund-Task.
- **Activate Changes:** `If-Match: *` erspart den ETag-Roundtrip.
- **Host anlegen ≠ Monitoring.** Nach dem Anlegen fehlt noch die Service-Discovery
  (`POST /domain-types/service_discovery_run/actions/start/invoke`, mode `fix_all`) + Aktivieren.

### Bootstrap-Datei — geteilt, also niemals user-spezifisch

`bootstrap.json` wird **zentral geteilt** (Samba01, mit Fallback auf `%APPDATA%`).
Daraus folgt eine Regel, die schon einmal produktiv gebrochen wurde: **kein
aufgelöster Benutzerpfad darf in die Datei geschrieben werden.** Genau das war
passiert — `SharedSettingsPath` enthielt `C:\Users\OsteL\AppData\Roaming\…`, jeder
andere Nutzer erbte den Pfad und die App **starb** beim Speichern der Einstellungen
(`DirectoryNotFoundException` aus dem RelayCommand → Avalonia-Dispatcher → Prozessende).

- `SharedSettingsPath` leer = user-lokal, und das ist der Default. `SettingsPathResolver`
  expandiert Umgebungsvariablen und verwirft Pfade, die in ein **fremdes** Benutzerprofil
  zeigen (UNC und `D:\…` bleiben unangetastet — die sind Absicht).
- `TryLoad` darf **nicht** wieder auf „SharedSettingsPath muss gesetzt sein" prüfen:
  dadurch galt die Datei als kaputt und wurde mit einem aufgelösten Profilpfad
  überschrieben — der Weg, auf dem der Fehler entstand.
- Schreibende Zugriffe auf Settings **immer** absichern. `SettingsViewModel.Save`
  fängt jetzt und lässt den Dialog offen; ein Schreibfehler darf nie die App beenden.

### Zentrale Datenbank (`CheckMK_Copilot` auf FOC-SQL01)

Löst die geteilten Teile von `bootstrap.json` und `hosts.json` auf dem Samba-Share
ab. Schema und Begründungen stehen in [`db/README.md`](db/README.md), die Skripte in
`db/`. Vier Punkte, die nicht „aufgeräumt" werden dürfen:

1. **Keine EF-Migrationen, kein `Database.Migrate()`.** Das Schema pflegen die
   SQL-Skripte in `db/`, ausgeführt vom Admin mit `CheckMK_Copilot_SA` (db_owner).
   Die App läuft als `CheckMK_Copilot_Worker` (nur datareader/datawriter) und
   *prüft* nur `SchemaVersion` gegen `CockpitDbContext.ExpectedSchemaVersion`.
   50 Clients, die beim Start gleichzeitig DDL versuchen, wären in keiner Lesart
   gut — und die meisten dürfen es ohnehin nicht. Deshalb ist auch
   `EntityFrameworkCore.Design` bewusst **nicht** referenziert.
2. **Der Ausfall-Cache ist tragend, kein Beiwerk.** Der Grund, vom Share
   wegzugehen, war dessen Verfügbarkeit — also darf die DB nicht der nächste
   Engpass werden. `GlobalSettingsProvider` schreibt nach jedem Erfolg
   `%APPDATA%\Kroste\Checkmk\globals-cache.json` und fällt beim Ausfall darauf
   zurück (`SettingsOrigin.Cache`), erst danach auf eingebaute Vorgaben.
3. **`GlobalSetting` ist Schlüssel/Wert, nicht eine Spalte je Einstellung.**
   Eine neue Einstellung soll keinen DDL-Termin mit dem SA-Konto brauchen.
   Fehlende, leere und kaputte Werte fallen einzeln auf ihren Default zurück
   (`CockpitGlobals.FromRows`) — ein halb gepflegter Datenbestand darf den Start
   nicht verhindern.
4. **Secrets bleiben user-lokal.** Verbindungs-Secret (`settings.json`) und
   SSH-Passwörter (`ssh-creds.json`) gehören nicht in eine Tabelle, die 48 Leute
   lesen dürfen — unabhängig von TDE. Der Verbindungsstring in `database.json`
   **neben der EXE** ist **Verschleierung, kein Zugriffsschutz** — der Schlüssel
   steckt im Binary daneben. Deshalb heißen die Methoden `Obfuscate`/
   `Deobfuscate` und nicht Encrypt/Decrypt; nicht in „Verschlüsselung"
   umbenennen, das ist dieselbe Ehrlichkeit wie bei `secretBase64` im
   Viewer-Profil (§4). Erzeugt wird die Datei mit
   `Checkmk.App.exe --protect-db "<String>"`. Quellenreihenfolge:
   `db-dev.json` (%APPDATA%, Entwicklung) → `database.json` (neben der EXE,
   Ausrollweg) → `bootstrap.json`.

5. **`DbHostDomainStore` hält eine Momentaufnahme im Speicher.** `Load()` macht
   kein I/O — `HostContext.DomainFor` ruft es für *jeden* Hostnamen auf, als
   Datenbank-Roundtrip wäre das absurd. Aktualisiert wird beim Start
   (`RefreshAsync`) und nach jedem Schreiben. Schlägt das Lesen fehl, bleibt die
   alte Momentaufnahme stehen: eine leere Zuordnung wäre schlimmer als eine
   veraltete, weil dann jeder Host auf die Default-Domain fiele und Ping/RDP/SSH
   ins Leere liefen. `Save()` diffed gegen die Tabelle statt alles
   zurückzuschreiben — das war der Fehler der alten `hosts.json`.
6. **Die Übernahme aus `hosts.json` läuft genau einmal** (`ImportLegacyIfEmptyAsync`,
   nur bei komplett leerer Tabelle). Danach ist die Tabelle die Wahrheit und die
   Datei wird nie wieder gelesen — sonst überschriebe ein Rechner mit altem
   Dateistand später zentrale Änderungen.

`DbContext` ist nicht threadsicher — deshalb `CockpitDatabase.CreateContext()`
je Vorgang statt eines Singletons; Hintergrund-Refresh und UI greifen parallel zu.

**Transitives Pinning ist Pflicht** (`CentralPackageTransitivePinningEnabled`).
Ohne es verlangt der Graph `Microsoft.Extensions.*` in 9.0.11, 10.0.0 und 10.0.8
nebeneinander, und jede dieser Versionen müsste in diesem Netz einzeln von Hand
ins Offline-Bundle geholt werden. Mit Pinning gibt es genau eine Version je Paket.
Die `Microsoft.Extensions.*`-Einträge in `Directory.Packages.props` stehen
deshalb dort, obwohl kein Projekt sie direkt referenziert — nicht entfernen.

**Proxy-Falle beim Diagnostizieren:** Der Proxy inspiziert TLS und signiert mit
einer Firmen-CA aus dem **Windows**-Zertifikatspeicher. `curl` aus Git Bash hat
sein eigenes CA-Bundle, kennt sie nicht und bricht mit exit 35 ab — das sieht
aus wie eine Firewall-Sperre und ist keine. **Erreichbarkeit immer mit .NET
prüfen** (`curl -k` als Schnelltest). Diese Verwechslung hat schon einmal einen
Roadmap-Punkt aus einer Fehldiagnose entstehen lassen (§8.29).

**NuGet-Falle in diesem Netz:** Der Proxy liefert `.nupkg` von nuget.org mit
**403** aus (Metadaten/`index.json` kommen durch). Das ist im Gegensatz zum
obigen eine *echte* Sperre — sie trifft auch `dotnet restore`. Pakete kommen deshalb aus dem
Offline-Bundle `C:\NuGet-Local`. `Microsoft.Data.SqlClient` zieht die komplette
MSAL-/Azure-Identity-Kette nach (~15 Pakete), die wir bei einem SQL-Login nie
anfassen — vermeidbar ist das nicht, EF Cores SqlServer-Provider setzt sie voraus.
Und: NuGet löst transitive Abhängigkeiten auf die **niedrigste passende** Version
auf, nicht auf die höchste lokal vorhandene — ein neueres Paket im Ordner ersetzt
eine geforderte ältere Version also nicht.

## 6 · Abhängigkeiten — Fallen

- **Avalonia >= 12.1** (aktuell 12.1.0, nativer Wayland-Backend ab 12.1). Breaking vs. v11: `Avalonia.Diagnostics` ist raus →
  `AvaloniaUI.DiagnosticsSupport` (Debug-only). `Window.SystemDecorations` → `WindowDecorations`
  (`WindowDecorations.BorderOnly`). `TextBox.Watermark` → `PlaceholderText`.
  `Avalonia.Controls.DataGrid` und `AvaloniaUI.DiagnosticsSupport` haben eigene Versionskadenz.
- **FluentAssertions auf v7 pinnen** (`[7.2.2,8.0.0)`). v8 = kommerzielle Xceed-Lizenz.
  Bei Dependabot/Renovate die Obergrenze prüfen — automatische Updates heben den Pin sonst aus
  (Major-Bumps für FluentAssertions per `ignore` ausschließen).
- **`Microsoft.Toolkit.Uwp.Notifications`** zieht transitiv `System.Drawing.Common 4.7.0`
  hinein, das mit `GHSA-rxg9-xrhp-64gj` (kritisch) blockiert `NU1904` unter
  `TreatWarningsAsErrors`. Explizit auf **10.0.9** überschreiben.

## 7 · Projektstandard

Flach (kein `src/`), `.slnx`, CPM (`Directory.Packages.props`), `Directory.Build.props`
(net10, Nullable, `TreatWarningsAsErrors`, `RepositoryUrl github.com/LHP542/`), MinVer aus
Git-Tags (`v*`), `.editorconfig` (file-scoped namespaces), NLog (Secrets vor dem Loggen
maskieren), globaler Exception-Handler. **Single-TFM**: `Checkmk.App` und
`Checkmk.Core.Tests` targeten `net10.0-windows10.0.19041.0` (WinRT-Toasts +
DPAPI). `Checkmk.Core` bleibt `net10.0`. CI läuft auf `windows-latest`, Release
erzeugt bei Tag `v*` ausschließlich das Windows-ZIP.

**Release-Notes-Konvention:** Für ausführliche Notes eine Datei
`RELEASE_NOTES/<tag>.md` im Repo anlegen (Beispiel: `RELEASE_NOTES/v1.0.0.md`).
Der Release-Workflow liest sie bevorzugt; Fallback ist die Message des annotated
Git-Tags. `generate_release_notes` ist bewusst aus — sonst hängt GitHub redundant
den Commit-Log an.

## 8 · Roadmap (nach Priorität)

1. ✅ Ack + Downtime aus der Liste.
2. ✅ Host-Filter mit Regex + Favoriten (Include-Listen).
3. ✅ Zentrale Windows-Verbindungsdatei auf Fileshare (Samba01 542$).
4. ✅ Service Discovery für bestehende Hosts (Config-Tab: Host → `fix_all` → aktivieren).
5. ✅ Host-Detailansicht (Doppelklick oder Rechtsklick → eigenes Fenster).
6. ✅ Autoupdater (Phase 1): GitHub-Releases-Check + Statusleisten-Badge + Dialog.
   Phase 2 (Selbst-Ersetzen + signierter Manifest) siehe Punkt 17.
7. ✅ Bulk-Ack/Downtime (Status-Tab + Host-Detail: Ctrl/Shift-Klick auf Services →
   ein Kommentar, iterative Ausführung, Einzelfehler brechen den Bulk nicht ab).
8. ✅ Kommentare (Anzeige im Host-Detail + Add auf Host/Service).
   DB-Health-Board wurde als „durch Host-Filter mit Regex/Include-Liste ausreichend
   abgedeckt" verworfen — statt eines eigenen Tabs legt jeder DB-Admin sich einen
   Favoriten „DB-Server" an (Regex `.*sql.*|.*ora.*` oder Include-Liste der Instanzen)
   und sieht seine DBs in Status/Konfig gefiltert.
9. ✅ Baumansicht (Hosts → Services) mit OS-Pictogrammen (`OsDetection`).
10. ✅ Tray + Status-Notifications (WinRT-Toast, Action-Center-kompatibel).
11. ✅ CSV-Export + Freitext-Filter über Ausgabe/Alias.
12. ✅ IP-Fallback per Ping/DNS im Host-Detail, wenn Checkmk keine liefert.
13. ✅ Client-Aktualisierung (Kontextmenü, Remote-PowerShell, Agent-Deinstall/Install/Register)
    — seit v1.7.0 ausgelagert ins Plugin
    [`Checkmk-Plugin-AgentUpdater`](https://github.com/LHP542/Checkmk-Plugin-AgentUpdater).
14. ✅ **Client-Aktualisierung härten**: `Start-Process msiexec` mit `-PassThru`
    + Exit-Code-Prüfung. Wanderte mit dem Plugin-Auszug in v1.7.0 in dessen
    Default-Skript-Vorlage.
15. ✅ **Kommentare löschen** — `DeleteCommentAsync` mit Dual-Fallback:
    `POST /domain-types/comment/actions/delete/invoke` (`delete_type: "by_id"`) und bei
    404/405 `DELETE /objects/comment/{id}`. Roter ✕-Button an jedem Kommentar im Host-Detail.
16. ✅ **OS-Familie aus Custom Host Attribute** statt Agent-PluginOutput-Parse. Der
    HW/SW-Inventur-Weg wurde als Umweg verworfen — verlässlicher ist das Custom
    Attribute (z. B. „Operation System"), das auf Folder-Ebene gesetzt und vererbt
    wird. Umsetzung: `HostAttributes.AdditionalProperties` als Catch-All,
    `Bootstrap.HostOsAttributeKeys` als Kandidatenliste, `IHostOsCache` als
    prozessweiter Cache. StatusViewModel.OsFor bevorzugt Cache, fällt auf
    OsDetection zurück. Vollständige OS-Version (2022, RHEL 9 usw.) bleibt offen.
17. ✅ **Autoupdater Phase 2**: Selbst-Ersetzen des Binary (`UpdateInstaller`,
    Austausch per `.bat` nach dem Prozessende).

    Das **signierte Manifest** ist ebenfalls gebaut, aber **bewusst
    abgeschaltet** — das Bedrohungsmodell trägt in diesem Kreis nicht
    (48 Systemadministratoren, zwei Schreibberechtigte auf dem Kanal-Ordner).
    Begründung und die Bedingungen, unter denen sich das ändern würde, stehen
    in §4. **Kein offener Punkt.**

    **ECDSA P-256 statt des hier notierten Ed25519**: .NET 10 bringt kein
    Ed25519 mit (nachgemessen — `System.Security.Cryptography` kennt ML-DSA und
    SLH-DSA, aber kein Ed25519). Es nachzurüsten hieße BouncyCastle, und ein
    weiteres NuGet-Paket ist in diesem Netz teuer.
18. **DPAPI-NG mit AD-Gruppen-SID** — obsolet, seit die Verbindung wieder user-lokal
    liegt (DPAPI-CurrentUser reicht). Nur relevant, falls wir irgendwann doch wieder
    einen geteilten Store brauchen.
19. ✅ **Zweite Checkmk-Instanz (Schulen)** — verifiziert: gleicher Server, nur
    andere Site (`Schul_IT`). Umgesetzt als leichter Site-Umschalter in der Titelleiste
    (`ConnectionSettings.KnownSites` + `UpdateActiveSite`), statt vollem Profil-Manager.
    Volle benannte Verbindungsprofile bleiben offen für den Fall dass es doch ein
    zweiter Server wird.
20. **Verbindungsdaten wieder user-lokal** (fertig): Nach kurzem Fileshare-Experiment
    (SharedAes) zurück nach `%APPDATA%\Kroste\Checkmk\settings.json` (DPAPI-CurrentUser).
    Anmeldedaten gehören pro Nutzer; der SharedAes-Trick war nur Zufalls-Einsichts-Schutz,
    kein echter Zugriffsschutz. `hosts.json` (Domain-Zuordnung) bleibt zentral —
    das sind Metadaten, keine Secrets.
21. ✅ **Viewer-Modus für Nur-Gucker** — `viewer.json` neben der Exe (Verbindung,
    Spaltensatz, Start-Filter) schaltet Kiosk-Betrieb: nur Status-Tab, keine
    Schreibaktionen, keine Plugins. Details und die drei Nicht-Aufräumen-Punkte
    in §4. Ein Profil-Manager mit mehreren benannten Viewer-Sichten in *einer*
    Datei wurde nicht gebaut — eine Sicht pro Ausgabe ist die Verteil-Einheit.
22. ✅ **Spalten frei konfigurierbar** (Status-Tab, Normalmodus) — Rechtsklick auf die
    Kopfzeile, Checkbox-Liste, Drag zum Umsortieren, persistent in `columns.json`.
    Details und die drei Fallen in §4. Für die Host-Detail-Tabelle bewusst *nicht*
    umgesetzt: dort ist der Spaltensatz kurz und `host` wäre redundant.
23. ✅ **Refresh ohne Einfrieren** — Abruf/Parse/Filtern auf dem ThreadPool,
    Fortschrittsbalken mit Restzeit in der Statusleiste, Collection-Austausch per
    Reset statt Einzel-Adds. Details und die drei Nicht-Zurückbauen-Punkte in §4.
24. ✅ **Zentrale Datenbank statt Fileshare** (`CheckMK_Copilot` auf FOC-SQL01,
    EF Core 10). Die geteilten Teile von `bootstrap.json` und `hosts.json` liegen
    in Tabellen; `hosts.json` wird einmalig übernommen. Ausfall-Cache,
    Zwei-Konten-Modell, `database.json` neben der EXE. Details in §5.
    Gründe: Schreibrechte auf dem Share hatten nur wenige, und das
    Read-Modify-Write der ganzen `hosts.json` verlor bei zwei gleichzeitigen
    Bearbeitern lautlos Einträge.

### In Arbeit: Standort-Karte (Punkte 25–28)

Fachlicher Hintergrund: 1105 Hosts über Potsdam verteilt (Stadtverwaltung mit
Außenstellen), 48 Nutzer in Teams von 2–3 Personen (DB, Netzwerk, Backup, ESX,
Fileservice, AD, Exchange, …), Mehrfachmitgliedschaft normal. Ziel ist eine
Karte, auf der ein Bereich grün/gelb/rot den schlechtesten Status seiner Hosts
zeigt.

**Die tragende Entscheidung: geteilte Karte, Linse pro Team.** Ein Serverraum ist
ein physischer Ort — er wird **einmal** gezeichnet und ein Gerät **einmal**
zugeordnet. Was ein Team davon sieht, entscheidet allein sein Host-Filter. Die
Bereichsfarbe entsteht deshalb erst in der `TeamView`, nicht in `Area`:
schlechtester Status der Hosts, die im Bereich stehen **und** auf den Filter der
Sicht passen. Derselbe Raum ist für das DB-Team grün und für den Wachschutz rot,
wenn die USV Netzausfall meldet. Nicht auf „jedes Team zeichnet seine eigene
Karte" zurückbauen: dann driften acht Polygone desselben Raums auseinander, und
wer einen Switch umträgt, müsste es acht Teams sagen. `HostArea.HostName` ist
Primärschlüssel — genau ein Bereich pro Host — und trägt `AssignedBy`.

Teams sind **Organisation, kein Zugriffsschutz** (alle 48 dürfen alle Hosts
sehen, so gewollt). Admin-Zuordnung über `dbo.AppAdmin`; wer in keinem Team ist,
sieht alles.

25. ✅ **Bereiche ohne Karte** — Bereichsbaum, Zuweisung per Mehrfachauswahl,
    Status-Rollup nach oben, Sammelknoten „Ohne Bereich" als Arbeitsliste.
    Details und die vier Nicht-Wegoptimieren-Punkte in §4. `Area.GeometryJson`
    bleibt vorerst leer — die Zuordnung, die hier entsteht, muss für die Karte
    nicht noch einmal angefasst werden.
26. ✅ **Geteilte Filter** — `filter.json` ist in die Datenbank gezogen.

    Zuerst als **Team-Modell** gebaut (v1.11.0), dann in v1.17.0 durch den
    **Filter-Katalog mit Abonnement** ersetzt (Schema 7). Grund: Teams setzen
    gepflegte Mitgliederlisten voraus, und die entstehen nicht von selbst —
    gemessen am 2026-08-22 gab es 0 Teams und 0 Mitgliedschaften. Ein Abo
    braucht dagegen niemanden, der etwas pflegt.

    Das Team-Modell ist **entfernt**, nicht danebengestellt: Zwei Wege, einen
    Filter zu teilen, wären der sicherste Weg, dass niemand beide versteht.
    Details und die sechs Nicht-Wegvereinfachen-Punkte in §4.
27. **Karte** — eigenes Kachel-Canvas in Avalonia (Slippy-Map-Mathematik,
    Polygone als Overlay, Treffer-Erkennung für den Rechtsklick). **Kein
    WebView, kein Google Maps**: Maps Platform kostet pro Load, verbietet
    Kachel-Caching und schickt die Standorte der eigenen IT-Infrastruktur an
    Google — das übersteht keine Datenschutzprüfung einer Stadtverwaltung.

    **Quelle steht fest und ist verifiziert** (2026-08-21, aus dem Netz des
    Fachbereichs, mit `HttpClient`): Digitale Orthophotos 20 cm der LGB
    Brandenburg, Open Data unter dl-de/by-2.0. Namensnennung
    „© GeoBasis-DE/LGB, dl-de/by-2-0" ist Pflicht und gehört ins UI.

    ```
    WMS    https://isk.geobasis-bb.de/mapproxy/dop20c/service/wms
           LAYERS=bebb_dop20c   SRS=EPSG:3857   FORMAT=image/png
    WMTS   https://isk.geobasis-bb.de/mapproxy/dop20c_wmts/service
    ```

    **Den WMS-Weg nehmen, nicht WMTS.** Das WMTS-Matrix-Set `grid_3857` hat
    einen eigenen, auf Brandenburg beschränkten Ursprung — globale
    Slippy-Map-Kachelindizes laufen dort in `TileOutOfRange`. Über WMS
    `GetMap` gibt der Client die BBOX selbst vor, und die rechnet
    `WebMercator` ohnehin schon aus; MapProxy liefert trotzdem aus seinem
    Kachel-Cache. Verifiziert mit der Kachel z13/4393/2691 (Potsdam
    Innenstadt): 156 KB echtes Luftbild.

    Kachel-URL und Layer gehören in `GlobalSetting` — dann ist ein Wechsel
    der Quelle ein `UPDATE` und kein Rollout. Für die Campus-Ebene benennt
    `Area.MapLayerKey` die Rasterquelle je Bereich.

    ✅ **Fertig.** Kachelkarte, Polygon-Overlay mit Rollup-Einfärbung, Zeichnen,
    Treffererkennung, **Nachbearbeiten**, Kontextmenü auf der Karte und
    `MapLayerKey` je Bereich (Details in §4).
28. ✅ **Team-Sichten/Kiosk** — `viewer.json` kennt einen `map`-Abschnitt
    (`show`, `area`, `zoom`, `layer`, `tree`). Damit bleibt der Bereiche-Tab im
    Kiosk erhalten, **lesend**. Details in §4. Wie beim übrigen Viewer-Modus
    gilt: Sichtbarkeitsgrenzen sind Bedienkomfort, die echte Grenze ist die
    Checkmk-Rolle.

**Nicht gebaut und warum:** Koordinate je Host (unnötig — Hosts hängen an
Bereichen, Bereiche haben die Geometrie; spart Geocoding komplett).
`geography`-Spaltentyp (bräuchte NetTopologySuite als weiteres Paket, und wir
rechnen nichts räumlich — GeoJSON in `nvarchar(max)` reicht). Ein Dienst vor der
Datenbank (die Sichten sollen nur in der Anwendung sichtbar sein, also verbinden
sich die Clients direkt).

### Nach der Karte

29. ✅ **Update-Bezug über den Fileshare** — gebaut, aber aus einem anderen
    Grund als ursprünglich notiert.

    Seit 2026-08-21 ist
    `\\samba01\542$\5424_IT-Basis-Dienste\CheckMK\CheckMK_Copilot` der Kanal
    (`FileShareUpdateChecker`, Details in §4). **Nicht**, weil GitHub gesperrt
    wäre — das war die Fehldiagnose unten —, sondern weil ein Ordner im eigenen
    Netz der kürzere Weg ist: kein Proxy, kein Internetzugang nötig, und das
    Ausrollen ist ein Kopiervorgang.

    Der GitHub-Weg bleibt als Kanal wählbar; welcher gilt, entscheidet allein
    die Schreibweise von `UpdateChannelUrl`.

    Die Fehldiagnose von damals steht bewusst hier stehen, weil die *Lehre*
    weiter gilt:

    Ursprünglich notiert, weil GitHub angeblich vollständig blockiert war.
    Nachgemessen am 2026-08-21 mit `HttpClient` (also so, wie die Anwendung es
    sieht): `api.github.com` **200**, `github.com` **200**, und der Download des
    Release-ZIPs **206 Partial Content**. Der Update-Weg über GitHub
    funktioniert.

    Die Fehldiagnose kam von `curl`: Der Proxy inspiziert TLS und signiert mit
    einer Firmen-CA, die im **Windows**-Zertifikatspeicher liegt. `curl` aus
    Git Bash bringt sein eigenes CA-Bundle mit, kennt sie nicht und bricht mit
    exit 35 ab — CONNECT-Tunnel steht, dann Stille. Das sieht aus wie eine
    Firewall-Sperre und ist keine. **Erreichbarkeit deshalb nie mit `curl`
    beurteilen, sondern mit .NET** (`curl -k` reicht als Schnelltest).

    Echte Sperre bleibt allein der `.nupkg`-Download von nuget.org: dort
    antwortet der Server mit **403**, und daran scheitert auch `dotnet restore`
    — Inhaltssperre, kein Zertifikatsproblem. Das Offline-Bundle bleibt also.

    Klemmt der Update-Check, liegt es also nicht am Netz — zuerst ins Log sehen.

## 9 · Deal

Lars liefert Ideen, Claude implementiert. Immer auf frischem `origin/main` aufsetzen, Änderungen
als Commit/Patch liefern (kein Push aus der Sandbox möglich).
