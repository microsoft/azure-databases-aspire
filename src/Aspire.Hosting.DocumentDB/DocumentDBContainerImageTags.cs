// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.DocumentDB;

internal static class DocumentDBContainerImageTags
{
    /// <remarks>ghcr.io/documentdb</remarks>
    public const string Registry = "ghcr.io/documentdb";

    /// <remarks>documentdb/documentdb-local</remarks>
    public const string Image = "documentdb/documentdb-local";

    /// <remarks>0.109.0</remarks>
    public const string DocumentDBVersion = "0.109.0";

    public const DocumentDBPostgreSqlVersion DefaultPostgreSqlVersion = DocumentDBPostgreSqlVersion.PG17;

    /// <remarks>pg17-0.109.0</remarks>
    public static string Tag => GetTag(DefaultPostgreSqlVersion);

    public static string GetTag(DocumentDBPostgreSqlVersion pgVersion)
    {
        var prefix = pgVersion switch
        {
            DocumentDBPostgreSqlVersion.PG16 => "pg16",
            DocumentDBPostgreSqlVersion.PG17 => "pg17",
            DocumentDBPostgreSqlVersion.PG18 => "pg18",
            _ => throw new ArgumentOutOfRangeException(nameof(pgVersion), pgVersion, "Unsupported PostgreSQL version.")
        };
        return $"{prefix}-{DocumentDBVersion}";
    }
}
