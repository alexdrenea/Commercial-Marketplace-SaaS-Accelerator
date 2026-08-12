// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See LICENSE file in the project root for license information.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Azure;
using Marketplace.SaaS.Accelerator.Services.Models;
using Microsoft.Marketplace.SaaS.Models;

namespace Marketplace.SaaS.Accelerator.Services.Contracts;

/// <summary>
/// Direct authenticated HTTP client for Marketplace Fulfillment operations.
/// Used when FulfillmentMode is Hybrid or Direct to avoid the SDK's
/// ArgumentOutOfRangeException on unknown termUnit enum values.
/// </summary>
public interface IMarketplaceDirectClient
{
    /// <summary>Gets a subscription by id asynchronously, preserving raw termUnit strings.</summary>
    Task<SubscriptionResult> GetSubscriptionByIdAsync(Guid subscriptionId, CancellationToken cancellationToken = default);

    /// <summary>Gets a subscription by id synchronously, preserving raw termUnit strings.</summary>
    SubscriptionResult GetSubscriptionById(Guid subscriptionId);

    /// <summary>Lists all subscriptions asynchronously including pagination, preserving raw termUnit strings.</summary>
    Task<List<SubscriptionResult>> GetAllSubscriptionsAsync(CancellationToken cancellationToken = default);

    /// <summary>Lists all subscriptions synchronously including pagination, preserving raw termUnit strings.</summary>
    List<SubscriptionResult> GetAllSubscriptions();

    /// <summary>Resolves a marketplace token to a subscription.</summary>
    Task<ResolvedSubscriptionResult> ResolveAsync(string marketplaceToken, CancellationToken cancellationToken = default);

    /// <summary>Lists available plans for a subscription.</summary>
    Task<List<PlanDetailResultExtension>> GetAllPlansForSubscriptionAsync(Guid subscriptionId, CancellationToken cancellationToken = default);

    /// <summary>Activates a subscription via direct HTTP POST.</summary>
    Task<Response> ActivateSubscriptionAsync(Guid subscriptionId, string planId, CancellationToken cancellationToken = default);

    /// <summary>Changes the plan for a subscription via direct HTTP PATCH.</summary>
    Task<SubscriptionUpdateResult> ChangePlanForSubscriptionAsync(Guid subscriptionId, string planId, CancellationToken cancellationToken = default);

    /// <summary>Changes the quantity for a subscription via direct HTTP PATCH.</summary>
    Task<SubscriptionUpdateResult> ChangeQuantityForSubscriptionAsync(Guid subscriptionId, int? quantity, CancellationToken cancellationToken = default);

    /// <summary>Deletes a subscription via direct HTTP DELETE.</summary>
    Task<SubscriptionUpdateResult> DeleteSubscriptionAsync(Guid subscriptionId, CancellationToken cancellationToken = default);

    /// <summary>Gets operation status via direct HTTP GET.</summary>
    Task<OperationResult> GetOperationStatusAsync(Guid subscriptionId, Guid operationId, CancellationToken cancellationToken = default);

    /// <summary>Patches operation status via direct HTTP PATCH.</summary>
    Task<Response> PatchOperationStatusAsync(Guid subscriptionId, Guid operationId, UpdateOperationStatusEnum status, CancellationToken cancellationToken = default);
}
