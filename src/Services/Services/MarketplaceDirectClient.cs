// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See LICENSE file in the project root for license information.
#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Azure;
using Azure.Core;
using Marketplace.SaaS.Accelerator.Services.Configurations;
using Marketplace.SaaS.Accelerator.Services.Contracts;
using Marketplace.SaaS.Accelerator.Services.Exceptions;
using Marketplace.SaaS.Accelerator.Services.Models;
using Microsoft.Marketplace.SaaS.Models;

namespace Marketplace.SaaS.Accelerator.Services.Services;

/// <summary>
/// Fulfillment read operations implemented directly via authenticated HttpClient.
/// Avoids the SDK ArgumentOutOfRangeException for unknown termUnit enum values
/// (e.g. "P10Y") by deserializing all enum-like API fields as strings.
/// Targets Marketplace API version 2018-08-31.
/// </summary>
public class MarketplaceDirectClient : IMarketplaceDirectClient
{
    private const string ApiVersion = "2018-08-31";
    private const string MarketplaceScope = "20e940b3-4c77-4b0b-9a53-9e16a1b010a7/.default";

    private readonly HttpClient _httpClient;
    private readonly TokenCredential _credential;
    private readonly ILogger _logger;
    private readonly Uri _baseUri;

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// Initializes a new instance of <see cref="MarketplaceDirectClient"/>.
    /// </summary>
    /// <param name="httpClientFactory">Factory to create named HttpClient instances.</param>
    /// <param name="credential">Azure credential used to obtain bearer tokens.</param>
    /// <param name="config">SaaS API client configuration.</param>
    /// <param name="logger">Application logger.</param>
    public MarketplaceDirectClient(
        IHttpClientFactory httpClientFactory,
        TokenCredential credential,
        SaaSApiClientConfiguration config,
        ILogger logger)
    {
        _httpClient = httpClientFactory.CreateClient(nameof(MarketplaceDirectClient));
        _credential = credential;
        _logger = logger;
        _baseUri = GetBaseUri(config.FulFillmentAPIBaseURL);
    }

    /// <inheritdoc />
    public async Task<SubscriptionResult> GetSubscriptionByIdAsync(
        Guid subscriptionId,
        CancellationToken cancellationToken = default)
    {
        _logger?.Info($"[Direct] GetSubscriptionByIdAsync: {subscriptionId}");
        var url = BuildUrl($"saas/subscriptions/{subscriptionId}");
        using var request = await BuildRequestAsync(HttpMethod.Get, url, correlationId: Guid.NewGuid(), cancellationToken);
        using var response = await SendAsync(request, MarketplaceActionEnum.GET_SUBSCRIPTION, cancellationToken);
        var dto = await DeserializeAsync<DirectSubscriptionDto>(response, cancellationToken);
        return MapSubscription(dto);
    }

    /// <inheritdoc />
    public async Task<List<SubscriptionResult>> GetAllSubscriptionsAsync(
        CancellationToken cancellationToken = default)
    {
        _logger?.Info("[Direct] GetAllSubscriptionsAsync");
        var results = new List<SubscriptionResult>();
        string? nextLink = BuildUrl("saas/subscriptions");
        var visitedLinks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        while (nextLink != null)
        {
            if (!visitedLinks.Add(nextLink))
                throw new MarketplaceException("Marketplace pagination returned a repeated continuation link.");

            using var request = await BuildRequestAsync(HttpMethod.Get, nextLink, correlationId: Guid.NewGuid(), cancellationToken);
            using var response = await SendAsync(request, MarketplaceActionEnum.GET_ALL_SUBSCRIPTIONS, cancellationToken);
            var page = await DeserializeOptionalAsync<DirectSubscriptionListDto>(response, cancellationToken);
            if (page == null)
                break;

            if (page.Subscriptions != null)
                foreach (var s in page.Subscriptions)
                    results.Add(MapSubscription(s));
            nextLink = NormalizeNextLink(page.NextLink);
        }

        return results;
    }

    /// <inheritdoc />
    public async Task<ResolvedSubscriptionResult> ResolveAsync(
        string marketplaceToken,
        CancellationToken cancellationToken = default)
    {
        _logger?.Info("[Direct] ResolveAsync");
        var url = BuildUrl("saas/subscriptions/resolve");
        using var request = await BuildRequestAsync(HttpMethod.Post, url, correlationId: Guid.NewGuid(), cancellationToken);
        // Resolve requires the marketplace token in the x-ms-marketplace-token header; body is empty.
        request.Headers.TryAddWithoutValidation("x-ms-marketplace-token", marketplaceToken);
        using var response = await SendAsync(request, MarketplaceActionEnum.RESOLVE, cancellationToken);
        var dto = await DeserializeAsync<DirectResolveDto>(response, cancellationToken);
        return MapResolvedSubscription(dto);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Safe in ASP.NET Core: the runtime does not install a SynchronizationContext on
    /// request threads, so blocking on a Task.Run delegate avoids the classic deadlock.
    /// </remarks>
    public SubscriptionResult GetSubscriptionById(Guid subscriptionId)
        => Task.Run(() => GetSubscriptionByIdAsync(subscriptionId)).GetAwaiter().GetResult();

    /// <inheritdoc />
    /// <remarks>
    /// Safe in ASP.NET Core: the runtime does not install a SynchronizationContext on
    /// request threads, so blocking on a Task.Run delegate avoids the classic deadlock.
    /// </remarks>
    public List<SubscriptionResult> GetAllSubscriptions()
        => Task.Run(() => GetAllSubscriptionsAsync()).GetAwaiter().GetResult();

    /// <inheritdoc />
    public async Task<List<PlanDetailResultExtension>> GetAllPlansForSubscriptionAsync(
        Guid subscriptionId,
        CancellationToken cancellationToken = default)
    {
        _logger?.Info($"[Direct] GetAllPlansForSubscriptionAsync: {subscriptionId}");
        var url = BuildUrl($"saas/subscriptions/{subscriptionId}/listAvailablePlans");
        using var request = await BuildRequestAsync(HttpMethod.Get, url, correlationId: Guid.NewGuid(), cancellationToken);
        using var response = await SendAsync(request, MarketplaceActionEnum.GET_ALL_PLANS, cancellationToken);
        var dto = await DeserializeAsync<DirectAvailablePlansDto>(response, cancellationToken);
        return MapPlans(dto.Plans);
    }

    /// <inheritdoc />
    public async Task<Response> ActivateSubscriptionAsync(
        Guid subscriptionId,
        string planId,
        CancellationToken cancellationToken = default)
    {
        planId = planId?.Replace(Environment.NewLine, string.Empty);
        _logger?.Info($"[Direct] ActivateSubscriptionAsync: {subscriptionId}");
        var url = BuildUrl($"saas/subscriptions/{subscriptionId}/activate");
        using var request = await BuildRequestAsync(HttpMethod.Post, url, Guid.NewGuid(), cancellationToken);
        request.Content = new StringContent(
            JsonSerializer.Serialize(new { planId }, _jsonOptions),
            Encoding.UTF8, "application/json");
        var response = await SendAsync(request, MarketplaceActionEnum.ACTIVATE, cancellationToken);
        return new DirectHttpResponse(response);
    }

    /// <inheritdoc />
    public async Task<SubscriptionUpdateResult> ChangePlanForSubscriptionAsync(
        Guid subscriptionId,
        string planId,
        CancellationToken cancellationToken = default)
    {
        planId = planId?.Replace(Environment.NewLine, string.Empty);
        _logger?.Info($"[Direct] ChangePlanForSubscriptionAsync: {subscriptionId} plan={planId}");
        var url = BuildUrl($"saas/subscriptions/{subscriptionId}");
        using var request = await BuildRequestAsync(HttpMethod.Patch, url, Guid.NewGuid(), cancellationToken);
        request.Content = new StringContent(
            JsonSerializer.Serialize(new { planId }, _jsonOptions),
            Encoding.UTF8, "application/json");
        using var response = await SendAsync(request, MarketplaceActionEnum.CHANGE_PLAN, cancellationToken);
        return new SubscriptionUpdateResult
        {
            OperationIdFromClientLib = ExtractOperationId(response, "operation-location", subscriptionId),
        };
    }

    /// <inheritdoc />
    public async Task<SubscriptionUpdateResult> ChangeQuantityForSubscriptionAsync(
        Guid subscriptionId,
        int? quantity,
        CancellationToken cancellationToken = default)
    {
        _logger?.Info($"[Direct] ChangeQuantityForSubscriptionAsync: {subscriptionId} qty={quantity}");
        var url = BuildUrl($"saas/subscriptions/{subscriptionId}");
        using var request = await BuildRequestAsync(HttpMethod.Patch, url, Guid.NewGuid(), cancellationToken);
        request.Content = new StringContent(
            JsonSerializer.Serialize(new { quantity }, _jsonOptions),
            Encoding.UTF8, "application/json");
        using var response = await SendAsync(request, MarketplaceActionEnum.CHANGE_QUANTITY, cancellationToken);
        return new SubscriptionUpdateResult
        {
            OperationIdFromClientLib = ExtractOperationId(response, "operation-location", subscriptionId),
        };
    }

    /// <inheritdoc />
    public async Task<SubscriptionUpdateResult> DeleteSubscriptionAsync(
        Guid subscriptionId,
        CancellationToken cancellationToken = default)
    {
        _logger?.Info($"[Direct] DeleteSubscriptionAsync: {subscriptionId}");
        var url = BuildUrl($"saas/subscriptions/{subscriptionId}");
        using var request = await BuildRequestAsync(HttpMethod.Delete, url, Guid.NewGuid(), cancellationToken);
        using var response = await SendAsync(request, MarketplaceActionEnum.DELETE, cancellationToken);
        return new SubscriptionUpdateResult
        {
            OperationIdFromClientLib = ExtractOperationId(response, "operation-location", subscriptionId),
        };
    }

    /// <inheritdoc />
    public async Task<OperationResult> GetOperationStatusAsync(
        Guid subscriptionId,
        Guid operationId,
        CancellationToken cancellationToken = default)
    {
        _logger?.Info($"[Direct] GetOperationStatusAsync: sub={subscriptionId} op={operationId}");
        var url = BuildUrl($"saas/subscriptions/{subscriptionId}/operations/{operationId}");
        using var request = await BuildRequestAsync(HttpMethod.Get, url, Guid.NewGuid(), cancellationToken);
        using var response = await SendAsync(request, MarketplaceActionEnum.OPERATION_STATUS, cancellationToken);
        var dto = await DeserializeAsync<DirectOperationDto>(response, cancellationToken);
        return MapOperation(dto);
    }

    /// <inheritdoc />
    public async Task<Response> PatchOperationStatusAsync(
        Guid subscriptionId,
        Guid operationId,
        UpdateOperationStatusEnum status,
        CancellationToken cancellationToken = default)
    {
        _logger?.Info($"[Direct] PatchOperationStatusAsync: sub={subscriptionId} op={operationId} status={status}");
        var url = BuildUrl($"saas/subscriptions/{subscriptionId}/operations/{operationId}");
        using var request = await BuildRequestAsync(HttpMethod.Patch, url, Guid.NewGuid(), cancellationToken);
        request.Content = new StringContent(
            JsonSerializer.Serialize(new { status = status.ToString() }, _jsonOptions),
            Encoding.UTF8, "application/json");
        var response = await SendAsync(request, MarketplaceActionEnum.UPDATE_OPERATION_STATUS, cancellationToken);
        return new DirectHttpResponse(response);
    }

    // ── Private helpers ──────────────────────────────────────────────────────

    private string BuildUrl(string path)
    {
        var requestUri = new Uri(_baseUri, $"{path}?api-version={ApiVersion}");
        return requestUri.AbsoluteUri;
    }

    private static Uri GetBaseUri(string? configuredBaseUrl)
    {
        const string defaultBaseUrl = "https://marketplaceapi.microsoft.com/api/";
        if (!Uri.TryCreate(configuredBaseUrl, UriKind.Absolute, out var baseUri))
            baseUri = new Uri(defaultBaseUrl);

        if (baseUri.Scheme != Uri.UriSchemeHttps)
            throw new MarketplaceException("Marketplace Fulfillment API base URL must use HTTPS.");

        return new Uri(baseUri.AbsoluteUri.TrimEnd('/') + "/");
    }

    private async Task<HttpRequestMessage> BuildRequestAsync(
        HttpMethod method,
        string url,
        Guid correlationId,
        CancellationToken cancellationToken)
    {
        var token = await _credential.GetTokenAsync(
            new TokenRequestContext(new[] { MarketplaceScope }),
            cancellationToken);

        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
        request.Headers.TryAddWithoutValidation("x-ms-requestid", Guid.NewGuid().ToString());
        request.Headers.TryAddWithoutValidation("x-ms-correlationid", correlationId.ToString());
        return request;
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        MarketplaceActionEnum action,
        CancellationToken cancellationToken)
    {
        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(request, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.Error($"[Direct] HTTP transport error for {action}: {ex.Message}", ex);
            throw new MarketplaceException($"HTTP transport error for {action}", ex);
        }

        if (response.IsSuccessStatusCode)
            return response;

        string correlationHeader = response.Headers.Contains("x-ms-correlationid")
            ? string.Join(",", response.Headers.GetValues("x-ms-correlationid"))
            : "(none)";

        _logger?.Warn($"[Direct] {action} failed: HTTP {(int)response.StatusCode} correlation={correlationHeader}");

        try
        {
            ThrowForStatus(response.StatusCode, action);
            return response; // unreachable
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    private static void ThrowForStatus(HttpStatusCode status, MarketplaceActionEnum action)
    {
        switch (status)
        {
            case HttpStatusCode.Unauthorized:
            case HttpStatusCode.Forbidden:
                throw new MarketplaceException("Token invalid or expired. Please try again.", SaasApiErrorCode.Unauthorized);
            case HttpStatusCode.NotFound:
                throw new MarketplaceException($"Unable to find the request {action}", SaasApiErrorCode.NotFound);
            case HttpStatusCode.Conflict:
                throw new MarketplaceException($"Conflict for {action}", SaasApiErrorCode.Conflict);
            case HttpStatusCode.BadRequest:
                throw new MarketplaceException($"Bad request for {action}", SaasApiErrorCode.BadRequest);
            case (HttpStatusCode)429:
                throw new MarketplaceException($"Rate limited for {action} (HTTP 429)", SaasApiErrorCode.BadRequest);
            default:
                throw new MarketplaceException($"Unexpected HTTP {(int)status} for {action}");
        }
    }

    private static async Task<T> DeserializeAsync<T>(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        string json;
        try
        {
            json = await response.Content.ReadAsStringAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            throw new MarketplaceException("Failed to read response body", ex);
        }

        try
        {
            return JsonSerializer.Deserialize<T>(json, _jsonOptions)
                   ?? throw new MarketplaceException("Deserialised response was null");
        }
        catch (JsonException ex)
        {
            throw new MarketplaceException($"Malformed JSON in response: {ex.Message}", ex);
        }
    }

    private static async Task<T?> DeserializeOptionalAsync<T>(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
        where T : class
    {
        string json;
        try
        {
            json = await response.Content.ReadAsStringAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            throw new MarketplaceException("Failed to read response body", ex);
        }

        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            return JsonSerializer.Deserialize<T>(json, _jsonOptions)
                   ?? throw new MarketplaceException("Deserialised response was null");
        }
        catch (JsonException ex)
        {
            throw new MarketplaceException($"Malformed JSON in response: {ex.Message}", ex);
        }
    }

    private string ExtractOperationId(
        HttpResponseMessage response,
        string headerName,
        Guid expectedSubscriptionId)
    {
        if (!response.Headers.TryGetValues(headerName, out var vals))
            throw new MarketplaceException($"Marketplace response did not include the required {headerName} header.");

        var urls = new List<string>(vals);
        if (urls.Count == 1
            && Uri.TryCreate(urls[0], UriKind.Absolute, out var uri)
            && uri.Scheme == Uri.UriSchemeHttps
            && string.Equals(uri.Authority, _baseUri.Authority, StringComparison.OrdinalIgnoreCase))
        {
            var segments = uri.AbsolutePath.TrimEnd('/').Split('/');
            var last = segments.Length > 0 ? segments[^1] : null;
            if (last != null && Guid.TryParse(last, out var operationId))
            {
                var expectedUri = new Uri(
                    _baseUri,
                    $"saas/subscriptions/{expectedSubscriptionId}/operations/{operationId}");
                if (string.Equals(
                        uri.AbsolutePath.TrimEnd('/'),
                        expectedUri.AbsolutePath.TrimEnd('/'),
                        StringComparison.OrdinalIgnoreCase))
                    return operationId.ToString();
            }
        }

        throw new MarketplaceException($"Marketplace returned an invalid {headerName} header.");
    }

    /// <summary>
    /// Ensures a @nextLink that already carries api-version isn't double-appended.
    /// Returns null when the link is empty/missing.
    /// </summary>
    private string? NormalizeNextLink(string? nextLink)
    {
        if (string.IsNullOrWhiteSpace(nextLink))
            return null;

        if (!Uri.TryCreate(nextLink, UriKind.Absolute, out var nextUri)
            || nextUri.Scheme != Uri.UriSchemeHttps
            || !string.Equals(nextUri.Authority, _baseUri.Authority, StringComparison.OrdinalIgnoreCase)
            || !nextUri.AbsolutePath.StartsWith(_baseUri.AbsolutePath, StringComparison.OrdinalIgnoreCase))
        {
            throw new MarketplaceException("Marketplace returned an invalid continuation link.");
        }

        return nextUri.AbsoluteUri;
    }

    // ── Mapping helpers ──────────────────────────────────────────────────────

    private static SubscriptionResult MapSubscription(DirectSubscriptionDto s)
    {
        if (s.Id == null)
            throw new MarketplaceException("Subscription Id cannot be null");

        return new SubscriptionResult
        {
            Id = s.Id.Value,
            PublisherId = s.PublisherId,
            OfferId = s.OfferId,
            Name = s.Name,
            SaasSubscriptionStatus = ParseSubscriptionStatus(s.SaasSubscriptionStatus),
            PlanId = s.PlanId,
            Quantity = s.Quantity ?? 0,
            Purchaser = s.Purchaser == null ? new PurchaserResult() : new PurchaserResult
            {
                EmailId = s.Purchaser.EmailId,
                ObjectId = s.Purchaser.ObjectId ?? Guid.Empty,
                TenantId = s.Purchaser.TenantId ?? Guid.Empty,
            },
            Beneficiary = s.Beneficiary == null ? new BeneficiaryResult() : new BeneficiaryResult
            {
                EmailId = s.Beneficiary.EmailId ?? string.Empty,
                ObjectId = s.Beneficiary.ObjectId ?? Guid.Empty,
                TenantId = s.Beneficiary.TenantId ?? Guid.Empty,
            },
            Term = s.Term == null ? new TermResult() : new TermResult
            {
                TermUnit = s.Term.TermUnit, // raw string preserved
                StartDate = s.Term.StartDate ?? default,
                EndDate = s.Term.EndDate ?? default,
            },
        };
    }

    private static ResolvedSubscriptionResult MapResolvedSubscription(DirectResolveDto r)
    {
        if (r.Id == null)
            throw new MarketplaceException("Subscription Id cannot be null");

        return new ResolvedSubscriptionResult
        {
            SubscriptionId = r.Id.Value,
            SubscriptionName = r.SubscriptionName,
            OfferId = r.OfferId,
            PlanId = r.PlanId,
            Quantity = r.Quantity ?? 0,
        };
    }

    private static OperationResult MapOperation(DirectOperationDto operation)
    {
        if (!Enum.TryParse<Marketplace.SaaS.Accelerator.Services.Models.OperationStatusEnum>(
                operation.Status,
                ignoreCase: true,
                out var status))
        {
            throw new MarketplaceException("Marketplace returned an unknown operation status.");
        }

        return new OperationResult
        {
            ID = operation.Id?.ToString(),
            Status = status,
            Created = operation.TimeStamp?.UtcDateTime ?? default,
            SubscriptionId = operation.SubscriptionId?.ToString(),
            ActionType = operation.Action,
        };
    }

    private static List<PlanDetailResultExtension> MapPlans(List<DirectPlanDto>? plans)
    {
        var result = new List<PlanDetailResultExtension>();
        if (plans == null) return result;

        foreach (var p in plans)
        {
            var ext = new PlanDetailResultExtension
            {
                PlanId = p.PlanId,
                DisplayName = p.DisplayName,
                Description = p.Description,
                IsPrivate = p.IsPrivate ?? false,
                HasFreeTrials = p.HasFreeTrials ?? false,
                IsPerUserPlan = p.IsPricePerSeat ?? false,
                IsStopSell = p.IsStopSell ?? false,
                Market = p.Market,
                PlanComponents = MapPlanComponents(p.PlanComponents),
            };
            result.Add(ext);
        }
        return result;
    }

    private static Marketplace.SaaS.Accelerator.Services.Models.PlanComponents MapPlanComponents(DirectPlanComponentsDto? dto)
    {
        var components = new Marketplace.SaaS.Accelerator.Services.Models.PlanComponents
        {
            MeteringDimensions = new List<Marketplace.SaaS.Accelerator.Services.Models.MeteringDimension>(),
            RecurrentBillingTerms = new List<Marketplace.SaaS.Accelerator.Services.Models.RecurrentBillingTerm>(),
        };

        if (dto == null) return components;

        if (dto.MeteringDimensions != null)
        {
            foreach (var d in dto.MeteringDimensions)
            {
                components.MeteringDimensions.Add(new Marketplace.SaaS.Accelerator.Services.Models.MeteringDimension
                {
                    Id = d.Id,
                    Currency = d.Currency,
                    PricePerUnit = d.PricePerUnit,
                    UnitOfMeasure = d.UnitOfMeasure,
                    DisplayName = d.DisplayName,
                });
            }
        }

        if (dto.RecurrentBillingTerms != null)
        {
            foreach (var t in dto.RecurrentBillingTerms)
            {
                var term = new Marketplace.SaaS.Accelerator.Services.Models.RecurrentBillingTerm
                {
                    Currency = t.Currency,
                    Price = t.Price,
                    TermDescription = t.TermDescription,
                    TermUnit = t.TermUnit, // raw string preserved, e.g. "P1Y", "P10Y"
                    MeteredQuantityIncluded = new List<Marketplace.SaaS.Accelerator.Services.Models.MeteringedQuantityIncluded>(),
                };
                if (t.MeteredQuantityIncluded != null)
                {
                    foreach (var mq in t.MeteredQuantityIncluded)
                    {
                        term.MeteredQuantityIncluded.Add(new Marketplace.SaaS.Accelerator.Services.Models.MeteringedQuantityIncluded
                        {
                            DimensionId = mq.DimensionId,
                            Units = mq.Units,
                        });
                    }
                }
                components.RecurrentBillingTerms.Add(term);
            }
        }

        return components;
    }

    private static Marketplace.SaaS.Accelerator.Services.Models.SubscriptionStatusEnum ParseSubscriptionStatus(string? raw)
    {
        if (raw != null && Enum.TryParse<Marketplace.SaaS.Accelerator.Services.Models.SubscriptionStatusEnum>(raw, ignoreCase: true, out var status))
            return status;
        return Marketplace.SaaS.Accelerator.Services.Models.SubscriptionStatusEnum.PendingFulfillmentStart;
    }

    // ── Internal DTOs (string-safe) ───────────────────────────────────────

    internal class DirectSubscriptionDto
    {
        [JsonPropertyName("id")] public Guid? Id { get; set; }
        [JsonPropertyName("publisherId")] public string? PublisherId { get; set; }
        [JsonPropertyName("offerId")] public string? OfferId { get; set; }
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("saasSubscriptionStatus")] public string? SaasSubscriptionStatus { get; set; }
        [JsonPropertyName("planId")] public string? PlanId { get; set; }
        [JsonPropertyName("quantity")] public int? Quantity { get; set; }
        [JsonPropertyName("purchaser")] public DirectPurchaserDto? Purchaser { get; set; }
        [JsonPropertyName("beneficiary")] public DirectBeneficiaryDto? Beneficiary { get; set; }
        [JsonPropertyName("term")] public DirectTermDto? Term { get; set; }
    }

    internal class DirectTermDto
    {
        /// <summary>Raw term unit string; unknown values (e.g. P10Y) are preserved.</summary>
        [JsonPropertyName("termUnit")] public string? TermUnit { get; set; }
        [JsonPropertyName("startDate")] public DateTimeOffset? StartDate { get; set; }
        [JsonPropertyName("endDate")] public DateTimeOffset? EndDate { get; set; }
    }

    internal class DirectPurchaserDto
    {
        [JsonPropertyName("emailId")] public string? EmailId { get; set; }
        [JsonPropertyName("objectId")] public Guid? ObjectId { get; set; }
        [JsonPropertyName("tenantId")] public Guid? TenantId { get; set; }
    }

    internal class DirectBeneficiaryDto
    {
        [JsonPropertyName("emailId")] public string? EmailId { get; set; }
        [JsonPropertyName("objectId")] public Guid? ObjectId { get; set; }
        [JsonPropertyName("tenantId")] public Guid? TenantId { get; set; }
    }

    internal class DirectSubscriptionListDto
    {
        [JsonPropertyName("subscriptions")] public List<DirectSubscriptionDto>? Subscriptions { get; set; }
        /// <summary>OData-style continuation link for the next page.</summary>
        [JsonPropertyName("@nextLink")] public string? NextLink { get; set; }
    }

    internal class DirectResolveDto
    {
        [JsonPropertyName("id")] public Guid? Id { get; set; }
        [JsonPropertyName("subscriptionName")] public string? SubscriptionName { get; set; }
        [JsonPropertyName("offerId")] public string? OfferId { get; set; }
        [JsonPropertyName("planId")] public string? PlanId { get; set; }
        [JsonPropertyName("quantity")] public int? Quantity { get; set; }
    }

    internal class DirectOperationDto
    {
        [JsonPropertyName("id")] public Guid? Id { get; set; }
        [JsonPropertyName("subscriptionId")] public Guid? SubscriptionId { get; set; }
        [JsonPropertyName("action")] public string? Action { get; set; }
        [JsonPropertyName("timeStamp")] public DateTimeOffset? TimeStamp { get; set; }
        [JsonPropertyName("status")] public string? Status { get; set; }
    }

    internal class DirectAvailablePlansDto
    {
        [JsonPropertyName("plans")] public List<DirectPlanDto>? Plans { get; set; }
    }

    internal class DirectPlanDto
    {
        [JsonPropertyName("planId")] public string? PlanId { get; set; }
        [JsonPropertyName("displayName")] public string? DisplayName { get; set; }
        [JsonPropertyName("description")] public string? Description { get; set; }
        [JsonPropertyName("isPrivate")] public bool? IsPrivate { get; set; }
        [JsonPropertyName("hasFreeTrials")] public bool? HasFreeTrials { get; set; }
        [JsonPropertyName("isPricePerSeat")] public bool? IsPricePerSeat { get; set; }
        [JsonPropertyName("isStopSell")] public bool? IsStopSell { get; set; }
        [JsonPropertyName("market")] public string? Market { get; set; }
        [JsonPropertyName("planComponents")] public DirectPlanComponentsDto? PlanComponents { get; set; }
    }

    internal class DirectPlanComponentsDto
    {
        [JsonPropertyName("recurrentBillingTerms")]
        public List<DirectRecurrentBillingTermDto>? RecurrentBillingTerms { get; set; }

        [JsonPropertyName("meteringDimensions")]
        public List<DirectMeteringDimensionDto>? MeteringDimensions { get; set; }
    }

    internal class DirectRecurrentBillingTermDto
    {
        [JsonPropertyName("currency")] public string? Currency { get; set; }
        [JsonPropertyName("price")] public float? Price { get; set; }
        [JsonPropertyName("termDescription")] public string? TermDescription { get; set; }
        /// <summary>Raw term unit string; unknown values (e.g. P10Y) are preserved.</summary>
        [JsonPropertyName("termUnit")] public string? TermUnit { get; set; }
        [JsonPropertyName("meteredQuantityIncluded")]
        public List<DirectMeteredQuantityIncludedDto>? MeteredQuantityIncluded { get; set; }
    }

    internal class DirectMeteringDimensionDto
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("currency")] public string? Currency { get; set; }
        [JsonPropertyName("pricePerUnit")] public float? PricePerUnit { get; set; }
        [JsonPropertyName("unitOfMeasure")] public string? UnitOfMeasure { get; set; }
        [JsonPropertyName("displayName")] public string? DisplayName { get; set; }
    }

    internal class DirectMeteredQuantityIncludedDto
    {
        [JsonPropertyName("dimensionId")] public string? DimensionId { get; set; }
        [JsonPropertyName("units")] public string? Units { get; set; }
    }
}

/// <summary>
/// Minimal Azure.Response implementation backed by an HttpResponseMessage.
/// Takes ownership of the response; disposed when this instance is disposed.
/// </summary>
internal sealed class DirectHttpResponse : Response
{
    private readonly HttpResponseMessage _response;

    public DirectHttpResponse(HttpResponseMessage response) => _response = response;

    public override int Status => (int)_response.StatusCode;
    public override string ReasonPhrase => _response.ReasonPhrase;

    public override Stream ContentStream
    {
        get => _response.Content?.ReadAsStreamAsync().GetAwaiter().GetResult() ?? Stream.Null;
        set { }
    }

    public override string ClientRequestId
    {
        get => _response.Headers.TryGetValues("x-ms-requestid", out var vals)
               ? string.Join(",", vals) : null;
        set { }
    }

    protected override bool TryGetHeader(string name, out string value)
    {
        if (_response.Headers.TryGetValues(name, out var vals) ||
            (_response.Content?.Headers.TryGetValues(name, out vals) ?? false))
        {
            value = string.Join(",", vals);
            return true;
        }

        value = null;
        return false;
    }

    protected override bool TryGetHeaderValues(string name, out IEnumerable<string> values)
    {
        if (_response.Headers.TryGetValues(name, out values) ||
            (_response.Content?.Headers.TryGetValues(name, out values) ?? false))
            return true;
        values = null;
        return false;
    }

    protected override bool ContainsHeader(string name)
        => _response.Headers.Contains(name) ||
           (_response.Content?.Headers.Contains(name) ?? false);

    protected override IEnumerable<HttpHeader> EnumerateHeaders()
    {
        foreach (var h in _response.Headers)
            foreach (var v in h.Value)
                yield return new HttpHeader(h.Key, v);
        if (_response.Content != null)
            foreach (var h in _response.Content.Headers)
                foreach (var v in h.Value)
                    yield return new HttpHeader(h.Key, v);
    }

    public override void Dispose() => _response.Dispose();
}
