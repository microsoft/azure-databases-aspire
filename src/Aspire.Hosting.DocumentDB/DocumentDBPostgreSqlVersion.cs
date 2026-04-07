// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Specifies the PostgreSQL major version that backs a DocumentDB instance.
/// </summary>
public enum DocumentDBPostgreSqlVersion
{
    /// <summary>PostgreSQL 16.</summary>
    PG16,

    /// <summary>PostgreSQL 17 (default).</summary>
    PG17,

    /// <summary>PostgreSQL 18.</summary>
    PG18,
}
