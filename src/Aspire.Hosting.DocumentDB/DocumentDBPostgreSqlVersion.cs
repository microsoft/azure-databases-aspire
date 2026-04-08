// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Specifies the PostgreSQL major version that backs a DocumentDB instance.
/// </summary>
public enum DocumentDBPostgreSqlVersion
{
    /// <summary>PostgreSQL 17 (default).</summary>
    PG17 = 0,

    /// <summary>PostgreSQL 16.</summary>
    PG16 = 1,

    /// <summary>PostgreSQL 18. Reserved for future support.</summary>
    [Obsolete("PG18 is not supported yet. It will be enabled once the matching DocumentDB Local image is published.", error: true)]
    PG18 = 2,
}
