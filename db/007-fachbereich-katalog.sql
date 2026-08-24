/*
    007-fachbereich-katalog.sql — Filter-Katalog je Fachbereich mit Abonnement.

    Loest das Team-Modell aus Skript 002 ab. Der Unterschied ist nicht die
    Benennung, sondern das Prinzip:

      Teams:   Wer im Team ist, SIEHT die Filter des Teams.
               -> jemand muss Mitgliederlisten pflegen.
      Katalog: Wer einen Filter ABONNIERT, sieht ihn.
               -> niemand muss irgendetwas pflegen.

    Der Fachbereich ist damit ein reiner Ordnungsbegriff im Katalog, keine
    Zugriffsgrenze. Veroeffentlichen darf jeder — dieselbe Haltung wie ueberall
    hier: Organisation, kein Zugriffsschutz. Die echte Grenze ist die
    Checkmk-Rolle des Anwenders.

    Das Team-Modell wird entfernt und nicht daneben stehen gelassen: Es war
    nachweislich unbenutzt (0 Teams, 0 Mitgliedschaften, Stand 2026-08-22), und
    zwei Wege, einen Filter zu teilen, waeren der sicherste Weg, dass niemand
    beide versteht.

    Mit dem SA-Konto ausfuehren, nach 006-area-hosttag.sql. Idempotent.
*/

SET NOCOUNT ON;
GO

/* ---------------------------------------------------------------------------
   Fachbereich — die Gruppe im Katalog.
--------------------------------------------------------------------------- */
IF OBJECT_ID(N'dbo.Fachbereich', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.Fachbereich
    (
        FachbereichId int           NOT NULL IDENTITY(1,1)
            CONSTRAINT PK_Fachbereich PRIMARY KEY,
        Name          nvarchar(128) NOT NULL
            CONSTRAINT UQ_Fachbereich_Name UNIQUE,
        Description   nvarchar(400) NULL,
        CreatedAtUtc  datetime2(0)  NOT NULL
            CONSTRAINT DF_Fachbereich_CreatedAtUtc DEFAULT SYSUTCDATETIME()
    );
END
GO

/* Ein Startwert, damit der Katalog nicht leer und unbenutzbar dasteht.
   Weitere legt man in der Oberflaeche an. */
IF NOT EXISTS (SELECT 1 FROM dbo.Fachbereich WHERE Name = N'5424 IT-Basis-Dienste')
    INSERT INTO dbo.Fachbereich (Name, Description)
    VALUES (N'5424 IT-Basis-Dienste', N'Arbeitsgruppe IT-Basis-Dienste');
GO

/* ---------------------------------------------------------------------------
   HostFilter: TeamId -> FachbereichId.

   Wichtiger Unterschied zum alten Modell: OwnerUserName bleibt IMMER gesetzt,
   auch bei einem veroeffentlichten Filter. Ein Filter hat einen Autor — und der
   darf ihn spaeter aendern, waehrend alle anderen ihn nur abonnieren. Der alte
   CHECK (genau eins von beidem) faellt deshalb weg.

   FachbereichId NULL = persoenlich, gesetzt = im Katalog veroeffentlicht.
--------------------------------------------------------------------------- */
IF COL_LENGTH('dbo.HostFilter', 'FachbereichId') IS NULL
    ALTER TABLE dbo.HostFilter ADD FachbereichId int NULL;
GO

IF EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_HostFilter_Owner')
    ALTER TABLE dbo.HostFilter DROP CONSTRAINT CK_HostFilter_Owner;
GO

/* Bestandsdaten: Team-Filter gab es keine (0 Teams), aber falls doch, bekommen
   sie den Standard-Fachbereich statt ins Nichts zu zeigen. */
IF COL_LENGTH('dbo.HostFilter', 'TeamId') IS NOT NULL
BEGIN
    DECLARE @default int = (SELECT TOP 1 FachbereichId FROM dbo.Fachbereich
                            WHERE Name = N'5424 IT-Basis-Dienste');

    EXEC sp_executesql N'
        UPDATE dbo.HostFilter
           SET FachbereichId = @d
         WHERE TeamId IS NOT NULL AND FachbereichId IS NULL;

        UPDATE dbo.HostFilter
           SET OwnerUserName = COALESCE(OwnerUserName, ChangedBy)
         WHERE OwnerUserName IS NULL;',
        N'@d int', @d = @default;
END
GO

/* Autor ist ab jetzt Pflicht.

   ACHTUNG: Auf der Spalte liegt IX_HostFilter_Owner aus Skript 002. SQL Server
   laesst ALTER COLUMN nicht zu, solange ein Index darauf zeigt
   (Meldung 5074/4922) — der Index muss weg, die Spalte umgestellt und der Index
   neu angelegt werden. Beim ersten Lauf ist genau das schiefgegangen. */
IF EXISTS (SELECT 1 FROM sys.columns
           WHERE object_id = OBJECT_ID('dbo.HostFilter')
             AND name = 'OwnerUserName' AND is_nullable = 1)
BEGIN
    UPDATE dbo.HostFilter SET OwnerUserName = ChangedBy WHERE OwnerUserName IS NULL;

    IF EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_HostFilter_Owner'
                                           AND object_id = OBJECT_ID('dbo.HostFilter'))
        DROP INDEX IX_HostFilter_Owner ON dbo.HostFilter;

    ALTER TABLE dbo.HostFilter ALTER COLUMN OwnerUserName nvarchar(128) NOT NULL;
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_HostFilter_Owner'
                                           AND object_id = OBJECT_ID('dbo.HostFilter'))
    CREATE INDEX IX_HostFilter_Owner ON dbo.HostFilter (OwnerUserName, Site);
GO

IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_HostFilter_Fachbereich')
    ALTER TABLE dbo.HostFilter ADD CONSTRAINT FK_HostFilter_Fachbereich
        FOREIGN KEY (FachbereichId) REFERENCES dbo.Fachbereich (FachbereichId);
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_HostFilter_Fachbereich'
                                           AND object_id = OBJECT_ID('dbo.HostFilter'))
    CREATE INDEX IX_HostFilter_Fachbereich ON dbo.HostFilter (FachbereichId, Site);
GO

/* ---------------------------------------------------------------------------
   Abonnement — der Kern des Ganzen.

   Kein Fremdschluessel auf einen Benutzer: Anmeldenamen stehen hier als
   blanker Text, wie ueberall in diesem Schema. Ein Nutzer, den es nicht mehr
   gibt, hinterlaesst eine verwaiste Zeile, die niemanden stoert.

   ON DELETE CASCADE auf den Filter: Wird ein Filter geloescht oder aus dem
   Katalog genommen, sind seine Abos gegenstandslos.
--------------------------------------------------------------------------- */
IF OBJECT_ID(N'dbo.HostFilterSubscription', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.HostFilterSubscription
    (
        HostFilterId   int           NOT NULL,
        UserName       nvarchar(128) NOT NULL,
        SubscribedAtUtc datetime2(0) NOT NULL
            CONSTRAINT DF_HostFilterSubscription_At DEFAULT SYSUTCDATETIME(),
        CONSTRAINT PK_HostFilterSubscription PRIMARY KEY (HostFilterId, UserName),
        CONSTRAINT FK_HostFilterSubscription_Filter FOREIGN KEY (HostFilterId)
            REFERENCES dbo.HostFilter (HostFilterId) ON DELETE CASCADE
    );
    CREATE INDEX IX_HostFilterSubscription_User ON dbo.HostFilterSubscription (UserName);
END
GO

/* ---------------------------------------------------------------------------
   Team-Modell entfernen.

   Reihenfolge: erst die Fremdschluessel-Spalte in HostFilter, dann die
   abhaengigen Tabellen, dann Team selbst. TeamView stammt aus Skript 002 und
   wurde nie benutzt.
--------------------------------------------------------------------------- */
IF EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_HostFilter_Team')
    ALTER TABLE dbo.HostFilter DROP CONSTRAINT FK_HostFilter_Team;
GO

IF EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_HostFilter_Team'
                                       AND object_id = OBJECT_ID('dbo.HostFilter'))
    DROP INDEX IX_HostFilter_Team ON dbo.HostFilter;
GO

IF COL_LENGTH('dbo.HostFilter', 'TeamId') IS NOT NULL
    ALTER TABLE dbo.HostFilter DROP COLUMN TeamId;
GO

IF OBJECT_ID(N'dbo.TeamViewHost', N'U') IS NOT NULL DROP TABLE dbo.TeamViewHost;
GO
IF OBJECT_ID(N'dbo.TeamView', N'U') IS NOT NULL DROP TABLE dbo.TeamView;
GO
IF OBJECT_ID(N'dbo.TeamMember', N'U') IS NOT NULL DROP TABLE dbo.TeamMember;
GO

/* Area.OwningTeamId zeigt auf Team — ebenfalls nie benutzt, muss aber vor dem
   DROP weg. */
IF EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_Area_OwningTeam')
    ALTER TABLE dbo.Area DROP CONSTRAINT FK_Area_OwningTeam;
GO
IF COL_LENGTH('dbo.Area', 'OwningTeamId') IS NOT NULL
    ALTER TABLE dbo.Area DROP COLUMN OwningTeamId;
GO

IF OBJECT_ID(N'dbo.Team', N'U') IS NOT NULL DROP TABLE dbo.Team;
GO

/* ------------------------------------------------------------------------- */

DECLARE @missing nvarchar(1000) = N'';

IF OBJECT_ID(N'dbo.Fachbereich', N'U') IS NULL
    SET @missing += N'dbo.Fachbereich, ';
IF OBJECT_ID(N'dbo.HostFilterSubscription', N'U') IS NULL
    SET @missing += N'dbo.HostFilterSubscription, ';
IF COL_LENGTH('dbo.HostFilter', 'FachbereichId') IS NULL
    SET @missing += N'HostFilter.FachbereichId, ';
IF COL_LENGTH('dbo.HostFilter', 'TeamId') IS NOT NULL
    SET @missing += N'HostFilter.TeamId (haette entfernt werden muessen), ';
IF OBJECT_ID(N'dbo.Team', N'U') IS NOT NULL
    SET @missing += N'dbo.Team (haette entfernt werden muessen), ';

/* Nicht nur pruefen, ob Spalten DA sind, sondern ob sie den richtigen Zustand
   haben. Beim ersten Lauf scheiterte das ALTER COLUMN an einem abhaengigen
   Index — und die Pruefung hier winkte trotzdem durch, weil die Spalte ja
   existierte. Genau die Sorte Halbdurchlauf, gegen die diese Guards da sind. */
IF EXISTS (SELECT 1 FROM sys.columns
           WHERE object_id = OBJECT_ID('dbo.HostFilter')
             AND name = 'OwnerUserName' AND is_nullable = 1)
    SET @missing += N'HostFilter.OwnerUserName ist noch NULL-bar, '
                  + N'das ALTER COLUMN ist nicht durchgelaufen, ';
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_HostFilter_Owner'
                                           AND object_id = OBJECT_ID('dbo.HostFilter'))
    SET @missing += N'IX_HostFilter_Owner (nach dem ALTER neu anzulegen), ';

IF LEN(@missing) > 0
BEGIN
    DECLARE @msg nvarchar(1200) =
        N'007-fachbereich-katalog.sql UNVOLLSTAENDIG. Problem bei: '
        + LEFT(@missing, LEN(@missing) - 1)
        + N'. SchemaVersion wurde nicht hochgesetzt.';
    RAISERROR(@msg, 16, 1);
END
ELSE
BEGIN
    UPDATE dbo.SchemaVersion
       SET Version = 7, AppliedAtUtc = SYSUTCDATETIME(), AppliedBy = SUSER_SNAME()
     WHERE Id = 1 AND Version < 7;

    PRINT '007-fachbereich-katalog.sql angewendet (SchemaVersion = 7).';
END
GO
