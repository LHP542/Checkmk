/*
    008-filter-target.sql — Filter kann auf den Host-ALIAS statt den Hostnamen
    matchen.

    Hintergrund aus dem Betrieb: Bei uns steht im Alias, wem ein Geraet
    zugeordnet ist — Werte wie „SchmidtT; WenzelM; SchmidtO; VolkJ; OsteL".
    Ein Filter auf den eigenen Anmeldenamen liefert damit „alle meine Rechner",
    ohne dass jemand eine Host-Liste pflegen muesste. Vorher brauchte es dafuer
    je Person einen Filter mit einer von Hand gepflegten Include-Liste.

    0 = Hostname (Vorgabe, bisheriges Verhalten), 1 = Alias.

    Betrifft nur den Regex. Die Include-Liste in HostFilterHost bleibt eine
    Liste von HOSTNAMEN: Sie entsteht aus „Auswahl als Favorit", also aus
    angeklickten Geraeten.

    Mit dem SA-Konto ausfuehren, nach 007-fachbereich-katalog.sql. Idempotent.
*/

SET NOCOUNT ON;
GO

IF COL_LENGTH('dbo.HostFilter', 'MatchTarget') IS NULL
    ALTER TABLE dbo.HostFilter ADD MatchTarget tinyint NOT NULL
        CONSTRAINT DF_HostFilter_MatchTarget DEFAULT 0;
GO

/* Nur 0 und 1 sind definiert. Ein Tippfehler beim Pflegen von Hand soll hier
   auffallen und nicht als „faellt auf Hostname zurueck" durchgehen. */
IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_HostFilter_MatchTarget')
    ALTER TABLE dbo.HostFilter ADD CONSTRAINT CK_HostFilter_MatchTarget
        CHECK (MatchTarget IN (0, 1));
GO

DECLARE @missing nvarchar(1000) = N'';

IF COL_LENGTH('dbo.HostFilter', 'MatchTarget') IS NULL
    SET @missing += N'HostFilter.MatchTarget, ';
IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_HostFilter_MatchTarget')
    SET @missing += N'CK_HostFilter_MatchTarget, ';

IF LEN(@missing) > 0
BEGIN
    DECLARE @msg nvarchar(1200) =
        N'008-filter-target.sql UNVOLLSTAENDIG. Es fehlen: '
        + LEFT(@missing, LEN(@missing) - 1)
        + N'. SchemaVersion wurde nicht hochgesetzt.';
    RAISERROR(@msg, 16, 1);
END
ELSE
BEGIN
    UPDATE dbo.SchemaVersion
       SET Version = 8, AppliedAtUtc = SYSUTCDATETIME(), AppliedBy = SUSER_SNAME()
     WHERE Id = 1 AND Version < 8;

    PRINT '008-filter-target.sql angewendet (SchemaVersion = 8).';
END
GO
