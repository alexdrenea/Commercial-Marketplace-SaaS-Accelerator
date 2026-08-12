// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See LICENSE file in the project root for license information.

// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See LICENSE file in the project root for license information.
#nullable enable

using System;
using System.Text.Json.Serialization;

namespace Marketplace.SaaS.Accelerator.Services.Models;

/// <summary>
/// Get TermResult.
/// </summary>
public class TermResult
{
    /// <summary>
    /// Gets or sets the end date.
    /// </summary>
    /// <value>
    /// The end date.
    /// </value>
    [JsonPropertyName("endDate")]
    public DateTimeOffset EndDate { get; set; }

    /// <summary>
    /// Gets or sets the start date.
    /// </summary>
    /// <value>
    /// The start date.
    /// </value>
    [JsonPropertyName("startDate")]
    public DateTimeOffset StartDate { get; set; }

    /// <summary>
    /// Gets or sets the term unit as a raw string (e.g. "P1M", "P1Y", "P10Y").
    /// Kept as string to survive unknown values returned by the Marketplace API
    /// without throwing ArgumentOutOfRangeException during deserialization.
    /// Null indicates the term unit was absent in the API response.
    /// </summary>
    /// <value>The term unit string.</value>
    [JsonPropertyName("termUnit")]
    public string? TermUnit { get; set; }
}