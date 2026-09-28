// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Xunit;

namespace Aspire.Hosting.DocumentDB.Tests;

/// <summary>
/// The classes that drive the feature-matrix AppHost through process-wide scenario variables, so
/// they must never run in parallel with one another.
/// </summary>
internal static class DocumentDBFeatureMatrixAppHostCollection
{
    public const string Name = "DocumentDB feature-matrix AppHost";
}

[CollectionDefinition(DocumentDBFeatureMatrixAppHostCollection.Name)]
public sealed class DocumentDBFeatureMatrixAppHostCollectionDefinition;
