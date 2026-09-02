// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Xunit;

namespace Aspire.Hosting.DocumentDB.Tests;

internal static class DocumentDBStorageGuardAppHostCollection
{
    public const string Name = "DocumentDB storage guard AppHost";
}

[CollectionDefinition(DocumentDBStorageGuardAppHostCollection.Name)]
public sealed class DocumentDBStorageGuardAppHostCollectionDefinition;
