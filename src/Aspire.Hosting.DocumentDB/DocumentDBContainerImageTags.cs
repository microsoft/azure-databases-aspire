// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Reflection;
using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.DocumentDB;

internal static class DocumentDBContainerImageTags
{
    private const string DocumentDBLocalImageVersionMetadataKey = "DocumentDBLocalImageVersion";

    /// <remarks>ghcr.io/documentdb</remarks>
    public const string Registry = "ghcr.io/documentdb";

    /// <remarks>documentdb/documentdb-local</remarks>
    public const string Image = "documentdb/documentdb-local";

    private static readonly string DocumentDBVersion = GetDocumentDBVersion();

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

    private static string GetDocumentDBVersion()
    {
        foreach (var attribute in typeof(DocumentDBContainerImageTags).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>())
        {
            if (string.Equals(attribute.Key, DocumentDBLocalImageVersionMetadataKey, StringComparison.Ordinal) &&
                !string.IsNullOrWhiteSpace(attribute.Value))
            {
                return attribute.Value;
            }
        }

        throw new InvalidOperationException($"Assembly metadata '{DocumentDBLocalImageVersionMetadataKey}' is required.");
    }
}
