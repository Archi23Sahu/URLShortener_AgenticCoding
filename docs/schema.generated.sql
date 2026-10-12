IF OBJECT_ID(N'[__EFMigrationsHistory]') IS NULL
BEGIN
    CREATE TABLE [__EFMigrationsHistory] (
        [MigrationId] nvarchar(150) NOT NULL,
        [ProductVersion] nvarchar(32) NOT NULL,
        CONSTRAINT [PK___EFMigrationsHistory] PRIMARY KEY ([MigrationId])
    );
END;
GO

BEGIN TRANSACTION;
GO

CREATE TABLE [ShortUrls] (
    [Id] bigint NOT NULL IDENTITY,
    [Code] varchar(12) COLLATE SQL_Latin1_General_CP1_CS_AS NOT NULL,
    [OriginalUrl] nvarchar(2048) NOT NULL,
    [OriginalUrlHash] char(64) NOT NULL,
    [CreatedAt] datetime2 NOT NULL,
    [ExpiresAt] datetime2 NOT NULL,
    [RowVersion] rowversion NOT NULL,
    CONSTRAINT [PK_ShortUrls] PRIMARY KEY ([Id])
);
GO

CREATE TABLE [ClickEvents] (
    [Id] bigint NOT NULL IDENTITY,
    [ShortUrlId] bigint NOT NULL,
    [ClickedAt] datetime2 NOT NULL,
    [VisitorHash] char(64) NOT NULL,
    [Country] char(2) NULL,
    CONSTRAINT [PK_ClickEvents] PRIMARY KEY ([Id]),
    CONSTRAINT [FK_ClickEvents_ShortUrls_ShortUrlId] FOREIGN KEY ([ShortUrlId]) REFERENCES [ShortUrls] ([Id]) ON DELETE CASCADE
);
GO

CREATE INDEX [IX_ClickEvents_ShortUrlId_ClickedAt] ON [ClickEvents] ([ShortUrlId], [ClickedAt]);
GO

CREATE INDEX [IX_ShortUrls_ExpiresAt] ON [ShortUrls] ([ExpiresAt]);
GO

CREATE INDEX [IX_ShortUrls_OriginalUrlHash] ON [ShortUrls] ([OriginalUrlHash]);
GO

CREATE UNIQUE INDEX [UX_ShortUrls_Code] ON [ShortUrls] ([Code]);
GO

INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
VALUES (N'20261008044138_InitialCreate', N'8.0.8');
GO

COMMIT;
GO

BEGIN TRANSACTION;
GO

ALTER TABLE [ClickEvents] ADD [Browser] varchar(30) NULL;
GO

INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
VALUES (N'20261010022547_AddBrowserToClickEvents', N'8.0.8');
GO

COMMIT;
GO

