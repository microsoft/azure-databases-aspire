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

    /// <summary>
    /// Default container tag for the <c>documentdb-local</c> image: <c>pg17-{Latest}</c>.
    /// Computed at runtime so it follows <see cref="DocumentDBVersions.Latest"/> rather than
    /// being baked in at compile time as a <see langword="const"/>.
    /// </summary>
    public static string Tag => $"pg{(int)DocumentDBPostgresVersion.Pg17}-{DocumentDBVersions.Latest}";
}
