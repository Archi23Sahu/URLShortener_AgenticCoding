/* ============================================================================
   URL Shortener: SQL Server schema (reference script)

   HAND-WRITTEN to match src/Configurations/*Configuration.cs.
   The authoritative script is the one EF Core generates:
       dotnet ef migrations script --project src -o docs/schema.generated.sql
   Compare the two. Differences are normally limited to the
   __EFMigrationsHistory table and constraint names.
   ============================================================================ */

IF DB_ID(N'UrlShortener') IS NULL
    CREATE DATABASE UrlShortener;
GO

USE UrlShortener;
GO

/* ---------------------------------------------------------------------------
   ShortUrls
   - Id is the identity value; the Base62 code is derived from it.
   - Code uses a CASE-SENSITIVE collation: Base62 treats 'aB' and 'Ab' as
     different codes, and the default SQL Server collation would not.
   - OriginalUrl is NOT indexed (nvarchar(2048) exceeds the 1700-byte index key
     limit). Duplicate detection uses the OriginalUrlHash index instead.
   - RowVersion is the optimistic-locking token used by PUT updates.
   --------------------------------------------------------------------------- */
CREATE TABLE dbo.ShortUrls
(
    Id              BIGINT        IDENTITY(1,1) NOT NULL,
    Code            VARCHAR(12)   COLLATE SQL_Latin1_General_CP1_CS_AS NOT NULL,
    OriginalUrl     NVARCHAR(2048) NOT NULL,
    OriginalUrlHash CHAR(64)      NOT NULL,   -- SHA-256 hex of the normalized URL
    CreatedAt       DATETIME2     NOT NULL,   -- UTC
    ExpiresAt       DATETIME2     NOT NULL,   -- UTC
    RowVersion      ROWVERSION    NOT NULL,
    CONSTRAINT PK_ShortUrls PRIMARY KEY CLUSTERED (Id)
);
GO

-- Redirect lookup by code; the unique constraint also catches Base62 collisions.
CREATE UNIQUE NONCLUSTERED INDEX UX_ShortUrls_Code
    ON dbo.ShortUrls (Code);

-- Duplicate detection. NON-unique on purpose: an expired link that the cleanup
-- job has not removed yet must not block shortening the same URL again.
CREATE NONCLUSTERED INDEX IX_ShortUrls_OriginalUrlHash
    ON dbo.ShortUrls (OriginalUrlHash);

-- Cleanup job scans for expired rows.
CREATE NONCLUSTERED INDEX IX_ShortUrls_ExpiresAt
    ON dbo.ShortUrls (ExpiresAt);
GO

/* ---------------------------------------------------------------------------
   ClickEvents
   - VisitorHash = SHA-256(IP + User-Agent + salt): counts unique visitors
     without storing raw IP addresses.
   - Deleting a ShortUrl cascades to its clicks.
   --------------------------------------------------------------------------- */
CREATE TABLE dbo.ClickEvents
(
    Id          BIGINT    IDENTITY(1,1) NOT NULL,
    ShortUrlId  BIGINT    NOT NULL,
    ClickedAt   DATETIME2 NOT NULL,        -- UTC
    VisitorHash CHAR(64)  NOT NULL,
    Country     CHAR(2)   NULL,            -- ISO 3166-1 alpha-2
    Browser     VARCHAR(30) NULL,          -- browser family; NULL for clicks recorded before migration AddBrowserToClickEvents
    CONSTRAINT PK_ClickEvents PRIMARY KEY CLUSTERED (Id),
    CONSTRAINT FK_ClickEvents_ShortUrls_ShortUrlId
        FOREIGN KEY (ShortUrlId) REFERENCES dbo.ShortUrls (Id) ON DELETE CASCADE
);
GO

-- Analytics queries per link and time range.
CREATE NONCLUSTERED INDEX IX_ClickEvents_ShortUrlId_ClickedAt
    ON dbo.ClickEvents (ShortUrlId, ClickedAt);
GO


/* ============================================================================
   VERIFICATION QUERIES (run after creating the schema or applying the migration)
   ============================================================================ */

-- 1. Case sensitivity: both inserts must succeed. If the second fails with a
--    unique-key violation, the Code column is NOT case-sensitive.
-- INSERT INTO dbo.ShortUrls (Code, OriginalUrl, OriginalUrlHash, CreatedAt, ExpiresAt)
-- VALUES ('aB1234', N'https://example.com/a', REPLICATE('a', 64), SYSUTCDATETIME(), DATEADD(DAY, 30, SYSUTCDATETIME()));
-- INSERT INTO dbo.ShortUrls (Code, OriginalUrl, OriginalUrlHash, CreatedAt, ExpiresAt)
-- VALUES ('Ab1234', N'https://example.com/b', REPLICATE('b', 64), SYSUTCDATETIME(), DATEADD(DAY, 30, SYSUTCDATETIME()));

-- 2. Cascade delete: deleting a link removes its click events.
-- INSERT INTO dbo.ClickEvents (ShortUrlId, ClickedAt, VisitorHash, Country)
-- SELECT Id, SYSUTCDATETIME(), REPLICATE('c', 64), 'US' FROM dbo.ShortUrls WHERE Code = 'aB1234';
-- DELETE FROM dbo.ShortUrls WHERE Code = 'aB1234';
-- SELECT COUNT(*) AS RemainingClicks FROM dbo.ClickEvents;   -- expect 0

-- 3. Index review: confirm the three ShortUrls indexes and the composite ClickEvents index exist.
-- SELECT t.name AS TableName, i.name AS IndexName, i.is_unique
-- FROM sys.indexes i JOIN sys.tables t ON t.object_id = i.object_id
-- WHERE t.name IN ('ShortUrls', 'ClickEvents') AND i.name IS NOT NULL
-- ORDER BY t.name, i.name;

-- 4. Query-performance check: redirect lookup should be an Index Seek on UX_ShortUrls_Code.
-- SET STATISTICS IO ON;
-- SELECT OriginalUrl, ExpiresAt FROM dbo.ShortUrls WHERE Code = 'aB1234';
