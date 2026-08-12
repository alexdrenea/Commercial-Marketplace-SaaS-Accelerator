// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See LICENSE file in the project root for license information.

namespace Marketplace.SaaS.Accelerator.Services.Models;

/// <summary>
/// Controls which HTTP transport is used for Marketplace Fulfillment operations.
/// Configure via environment variable SaaSApiConfiguration__FulfillmentMode.
/// Requires process/app restart to take effect; hot reload is not supported.
/// </summary>
public enum FulfillmentMode
{
    /// <summary>
    /// All Marketplace fulfillment operations use the Marketplace.SaaS.Client SDK (default).
    /// </summary>
    Sdk,

    /// <summary>
    /// The four term-bearing read operations (Resolve, GetSubscription sync/async,
    /// ListSubscriptions sync/async, ListAvailablePlans) use direct HTTP to preserve
    /// unknown termUnit values (e.g. "P10Y"). All writes and operation-status calls use the SDK.
    /// </summary>
    Hybrid,

    /// <summary>
    /// Every Marketplace fulfillment API operation uses direct HTTP.
    /// Unknown termUnit values (e.g. "P10Y") are preserved as-is.
    /// </summary>
    Direct,
}
