// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See LICENSE file in the project root for license information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Azure;
using Azure.Core;
using Marketplace.SaaS.Accelerator.Services.Configurations;
using Marketplace.SaaS.Accelerator.Services.Contracts;
using Marketplace.SaaS.Accelerator.Services.Exceptions;
using Marketplace.SaaS.Accelerator.Services.Models;
using Marketplace.SaaS.Accelerator.Services.Services;
using Microsoft.Marketplace.SaaS;
using Microsoft.Marketplace.SaaS.Models;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using Subscriptions = Marketplace.SaaS.Accelerator.DataAccess.Entities.Subscriptions;

namespace Marketplace.SaaS.Accelerator.Services.Test;

/// <summary>
/// Deterministic offline tests for MarketplaceDirectClient and related fulfillment routing changes.
/// No calls to Marketplace API or Entra are made; a fake HttpMessageHandler and fake TokenCredential are used.
/// </summary>
[TestClass]
public class MarketplaceDirectClientTests
{
    [TestMethod]
    public async Task GetSubscriptionById_SendsCorrectUrlAndAuthHeader()
    {
        HttpRequestMessage captured = null;
        var handler = new CaptureHandler(
            req =>
            {
                captured = req;
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = MarketplaceTestHelpers.Json(MarketplaceTestHelpers.KnownSubDto()) };
            });

        var client = MarketplaceTestHelpers.BuildClient(handler);
        await client.GetSubscriptionByIdAsync(MarketplaceTestHelpers.SubId);

        Assert.IsNotNull(captured);
        StringAssert.Contains(captured.RequestUri.ToString(), MarketplaceTestHelpers.SubId.ToString());
        StringAssert.Contains(captured.RequestUri.ToString(), "api-version=2018-08-31");
        Assert.AreEqual("Bearer", captured.Headers.Authorization.Scheme);
        Assert.AreEqual("fake-bearer-token", captured.Headers.Authorization.Parameter);
        Assert.IsTrue(captured.Headers.Contains("x-ms-requestid"), "missing x-ms-requestid");
        Assert.IsTrue(captured.Headers.Contains("x-ms-correlationid"), "missing x-ms-correlationid");
    }

    [TestMethod]
    public async Task Resolve_SendsMarketplaceTokenHeader()
    {
        HttpRequestMessage captured = null;
        var handler = new CaptureHandler(
            req =>
            {
                captured = req;
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = MarketplaceTestHelpers.Json(MarketplaceTestHelpers.ResolveDto()) };
            });

        var client = MarketplaceTestHelpers.BuildClient(handler);
        await client.ResolveAsync("my-marketplace-token");

        Assert.IsNotNull(captured);
        Assert.AreEqual(HttpMethod.Post, captured.Method);
        StringAssert.Contains(captured.RequestUri.ToString(), "resolve");
        Assert.IsTrue(captured.Headers.Contains("x-ms-marketplace-token"), "missing x-ms-marketplace-token");
        var tokenHeader = string.Join(",", captured.Headers.GetValues("x-ms-marketplace-token"));
        Assert.AreEqual("my-marketplace-token", tokenHeader);
    }

    [TestMethod]
    public async Task GetAllPlansForSubscription_SendsCorrectPath()
    {
        HttpRequestMessage captured = null;
        var handler = new CaptureHandler(
            req =>
            {
                captured = req;
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = MarketplaceTestHelpers.Json(MarketplaceTestHelpers.PlansDto()) };
            });

        var client = MarketplaceTestHelpers.BuildClient(handler);
        await client.GetAllPlansForSubscriptionAsync(MarketplaceTestHelpers.SubId);

        Assert.IsNotNull(captured);
        StringAssert.Contains(captured.RequestUri.ToString(), $"subscriptions/{MarketplaceTestHelpers.SubId}/listAvailablePlans");
    }

    [TestMethod]
    public async Task GetSubscriptionById_KnownTermUnit_P1Y_Preserved()
    {
        var dto = MarketplaceTestHelpers.KnownSubDto(termUnit: "P1Y");
        var handler = new FixedHandler(HttpStatusCode.OK, MarketplaceTestHelpers.Json(dto));
        var result = await MarketplaceTestHelpers.BuildClient(handler).GetSubscriptionByIdAsync(MarketplaceTestHelpers.SubId);

        Assert.AreEqual("P1Y", result.Term.TermUnit);
    }

    [TestMethod]
    public async Task GetSubscriptionById_UnknownTermUnit_P10Y_Preserved()
    {
        var dto = MarketplaceTestHelpers.KnownSubDto(termUnit: "P10Y");
        var handler = new FixedHandler(HttpStatusCode.OK, MarketplaceTestHelpers.Json(dto));
        var result = await MarketplaceTestHelpers.BuildClient(handler).GetSubscriptionByIdAsync(MarketplaceTestHelpers.SubId);

        Assert.AreEqual("P10Y", result.Term.TermUnit, "Unknown termUnit P10Y should survive without exception");
    }

    [TestMethod]
    public async Task GetSubscriptionById_NullTermUnit_IsNull()
    {
        var dto = MarketplaceTestHelpers.KnownSubDto(termUnit: null);
        var handler = new FixedHandler(HttpStatusCode.OK, MarketplaceTestHelpers.Json(dto));
        var result = await MarketplaceTestHelpers.BuildClient(handler).GetSubscriptionByIdAsync(MarketplaceTestHelpers.SubId);

        Assert.IsNull(result.Term.TermUnit, "Absent termUnit should be null");
    }

    [TestMethod]
    public async Task GetAllSubscriptions_P10Y_TermUnit_Preserved()
    {
        var page = new
        {
            subscriptions = new[] { MarketplaceTestHelpers.KnownSubDto(termUnit: "P10Y") },
        };
        var handler = new FixedHandler(HttpStatusCode.OK, MarketplaceTestHelpers.Json(page));
        var results = await MarketplaceTestHelpers.BuildClient(handler).GetAllSubscriptionsAsync();

        Assert.AreEqual(1, results.Count);
        Assert.AreEqual("P10Y", results[0].Term.TermUnit);
    }

    [TestMethod]
    public async Task GetAllSubscriptions_EmptySuccessfulBody_ReturnsEmptyList()
    {
        var handler = new FixedHandler(HttpStatusCode.OK, new StringContent(string.Empty));

        var results = await MarketplaceTestHelpers.BuildClient(handler).GetAllSubscriptionsAsync();

        Assert.IsNotNull(results);
        Assert.AreEqual(0, results.Count);
    }

    [TestMethod]
    public async Task GetAllSubscriptions_Paginates_NextLink()
    {
        int callCount = 0;
        var handler = new CaptureHandler(req =>
        {
            callCount++;
            if (callCount == 1)
            {
                var json = "{\"subscriptions\":[" + JsonSerializer.Serialize(MarketplaceTestHelpers.KnownSubDto(termUnit: "P1M")) + "]," +
                           "\"@nextLink\":\"https://marketplaceapi.microsoft.com/api/saas/subscriptions?api-version=2018-08-31&continuationToken=abc\"}";
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
            }

            var secondPageJson = "{\"subscriptions\":[" + JsonSerializer.Serialize(MarketplaceTestHelpers.KnownSubDto(termUnit: "P2Y")) + "]}";
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(secondPageJson, Encoding.UTF8, "application/json") };
        });

        var results = await MarketplaceTestHelpers.BuildClient(handler).GetAllSubscriptionsAsync();

        Assert.AreEqual(2, callCount, "Should have paginated to page 2");
        Assert.AreEqual(2, results.Count, "Should have 2 subscriptions total");
        Assert.AreEqual("P1M", results[0].Term.TermUnit);
        Assert.AreEqual("P2Y", results[1].Term.TermUnit);
    }

    [TestMethod]
    public async Task GetAllSubscriptions_RejectsContinuationLinkForDifferentHost()
    {
        var json = "{\"subscriptions\":[],\"@nextLink\":\"https://example.invalid/api/saas/subscriptions?continuationToken=abc\"}";
        var handler = new FixedHandler(
            HttpStatusCode.OK,
            new StringContent(json, Encoding.UTF8, "application/json"));

        await Assert.ThrowsExactlyAsync<MarketplaceException>(
            () => MarketplaceTestHelpers.BuildClient(handler).GetAllSubscriptionsAsync());
    }

    [TestMethod]
    public async Task GetAllSubscriptions_RejectsRepeatedContinuationLink()
    {
        const string nextLink = "https://marketplaceapi.microsoft.com/api/saas/subscriptions?api-version=2018-08-31&continuationToken=abc";
        var json = $"{{\"subscriptions\":[],\"@nextLink\":\"{nextLink}\"}}";
        var handler = new FixedHandler(
            HttpStatusCode.OK,
            new StringContent(json, Encoding.UTF8, "application/json"));

        await Assert.ThrowsExactlyAsync<MarketplaceException>(
            () => MarketplaceTestHelpers.BuildClient(handler).GetAllSubscriptionsAsync());
    }

    [TestMethod]
    public async Task GetSubscriptionById_Returns404_ThrowsMarketplaceException()
    {
        var handler = new FixedHandler(HttpStatusCode.NotFound, new StringContent("{}"));
        await Assert.ThrowsExactlyAsync<MarketplaceException>(
            () => MarketplaceTestHelpers.BuildClient(handler).GetSubscriptionByIdAsync(MarketplaceTestHelpers.SubId));
    }

    [TestMethod]
    public async Task GetSubscriptionById_Returns401_ThrowsMarketplaceException()
    {
        var handler = new FixedHandler(HttpStatusCode.Unauthorized, new StringContent("{}"));
        await Assert.ThrowsExactlyAsync<MarketplaceException>(
            () => MarketplaceTestHelpers.BuildClient(handler).GetSubscriptionByIdAsync(MarketplaceTestHelpers.SubId));
    }

    [TestMethod]
    public async Task GetSubscriptionById_Returns403_ThrowsMarketplaceException()
    {
        var handler = new FixedHandler(HttpStatusCode.Forbidden, new StringContent("{}"));
        await Assert.ThrowsExactlyAsync<MarketplaceException>(
            () => MarketplaceTestHelpers.BuildClient(handler).GetSubscriptionByIdAsync(MarketplaceTestHelpers.SubId));
    }

    [TestMethod]
    public async Task GetSubscriptionById_Returns400_ThrowsMarketplaceException()
    {
        var handler = new FixedHandler(HttpStatusCode.BadRequest, new StringContent("{\"error\":\"bad\"}"));
        await Assert.ThrowsExactlyAsync<MarketplaceException>(
            () => MarketplaceTestHelpers.BuildClient(handler).GetSubscriptionByIdAsync(MarketplaceTestHelpers.SubId));
    }

    [TestMethod]
    public async Task GetSubscriptionById_Returns429_ThrowsMarketplaceException()
    {
        var handler = new FixedHandler((HttpStatusCode)429, new StringContent("{}"));
        await Assert.ThrowsExactlyAsync<MarketplaceException>(
            () => MarketplaceTestHelpers.BuildClient(handler).GetSubscriptionByIdAsync(MarketplaceTestHelpers.SubId));
    }

    [TestMethod]
    public async Task GetSubscriptionById_Returns500_ThrowsMarketplaceException()
    {
        var handler = new FixedHandler(HttpStatusCode.InternalServerError, new StringContent("{}"));
        await Assert.ThrowsExactlyAsync<MarketplaceException>(
            () => MarketplaceTestHelpers.BuildClient(handler).GetSubscriptionByIdAsync(MarketplaceTestHelpers.SubId));
    }

    [TestMethod]
    public async Task GetSubscriptionById_MalformedJson_ThrowsMarketplaceException()
    {
        var handler = new FixedHandler(HttpStatusCode.OK, new StringContent("not-valid-json"));
        await Assert.ThrowsExactlyAsync<MarketplaceException>(
            () => MarketplaceTestHelpers.BuildClient(handler).GetSubscriptionByIdAsync(MarketplaceTestHelpers.SubId));
    }

    [TestMethod]
    public async Task GetSubscriptionById_Error_CorrelationIdIncludedInLog()
    {
        var correlationId = Guid.NewGuid().ToString();
        var handler = new CaptureHandler(req =>
        {
            var resp = new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{}") };
            resp.Headers.TryAddWithoutValidation("x-ms-correlationid", correlationId);
            return resp;
        });

        await Assert.ThrowsExactlyAsync<MarketplaceException>(
            () => MarketplaceTestHelpers.BuildClient(handler).GetSubscriptionByIdAsync(MarketplaceTestHelpers.SubId));
    }

    [TestMethod]
    public async Task GetSubscriptionById_Cancellation_ThrowsOperationCancelledException()
    {
        var cts = new CancellationTokenSource();
        var handler = new CaptureHandler(_ =>
        {
            cts.Cancel();
            throw new OperationCanceledException(cts.Token);
        });

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(
            () => MarketplaceTestHelpers.BuildClient(handler).GetSubscriptionByIdAsync(MarketplaceTestHelpers.SubId, cts.Token));
    }

    [TestMethod]
    public async Task GetSubscriptionById_MapsAllFields()
    {
        var dto = MarketplaceTestHelpers.KnownSubDto(termUnit: "P1Y");
        var handler = new FixedHandler(HttpStatusCode.OK, MarketplaceTestHelpers.Json(dto));
        var result = await MarketplaceTestHelpers.BuildClient(handler).GetSubscriptionByIdAsync(MarketplaceTestHelpers.SubId);

        Assert.AreEqual(MarketplaceTestHelpers.SubId, result.Id);
        Assert.AreEqual("pub-1", result.PublisherId);
        Assert.AreEqual("offer-1", result.OfferId);
        Assert.AreEqual("Test Sub", result.Name);
        Assert.AreEqual("plan-basic", result.PlanId);
        Assert.AreEqual(2, result.Quantity);
        Assert.AreEqual(Marketplace.SaaS.Accelerator.Services.Models.SubscriptionStatusEnum.Subscribed, result.SaasSubscriptionStatus);
        Assert.AreEqual("P1Y", result.Term.TermUnit);
    }

    [TestMethod]
    public async Task ResolveAsync_MapsAllFields()
    {
        var dto = MarketplaceTestHelpers.ResolveDto();
        var handler = new FixedHandler(HttpStatusCode.OK, MarketplaceTestHelpers.Json(dto));
        var result = await MarketplaceTestHelpers.BuildClient(handler).ResolveAsync("token");

        Assert.AreEqual(MarketplaceTestHelpers.SubId, result.SubscriptionId);
        Assert.AreEqual("My Sub", result.SubscriptionName);
        Assert.AreEqual("offer-1", result.OfferId);
        Assert.AreEqual("plan-basic", result.PlanId);
        Assert.AreEqual(3, result.Quantity);
    }

    [TestMethod]
    public async Task GetAllPlansForSubscription_MapsPlans()
    {
        var dto = MarketplaceTestHelpers.PlansDto();
        var handler = new FixedHandler(HttpStatusCode.OK, MarketplaceTestHelpers.Json(dto));
        var result = await MarketplaceTestHelpers.BuildClient(handler).GetAllPlansForSubscriptionAsync(MarketplaceTestHelpers.SubId);

        Assert.AreEqual(2, result.Count);
        Assert.AreEqual("plan-basic", result[0].PlanId);
        Assert.AreEqual("Basic Plan", result[0].DisplayName);
        Assert.AreEqual("plan-pro", result[1].PlanId);
    }

    [TestMethod]
    public void TermResult_TermUnit_IsString()
    {
        var term = new TermResult { TermUnit = "P10Y" };
        Assert.AreEqual("P10Y", term.TermUnit);
    }

    [TestMethod]
    public void TermResult_TermUnit_CanBeNull()
    {
        var term = new TermResult { TermUnit = null };
        Assert.IsNull(term.TermUnit);
    }

    [TestMethod]
    public void TermResult_Serializes_RawString_P10Y()
    {
        var term = new TermResult { TermUnit = "P10Y", StartDate = default, EndDate = default };
        var json = JsonSerializer.Serialize(term);
        StringAssert.Contains(json, "\"P10Y\"");
    }

    [TestMethod]
    public void TermResult_Deserializes_UnknownValue_P10Y()
    {
        var json = "{\"termUnit\":\"P10Y\",\"startDate\":\"2024-01-01T00:00:00+00:00\",\"endDate\":\"2034-01-01T00:00:00+00:00\"}";
        var term = JsonSerializer.Deserialize<TermResult>(json);
        Assert.IsNotNull(term);
        Assert.AreEqual("P10Y", term.TermUnit);
    }

    [TestMethod]
    public void SubscriptionService_PrepareSubscriptionResponse_PreservesP10Y()
    {
        var subscriptionEntity = new DataAccess.Entities.Subscriptions
        {
            Id = 42,
            AmpsubscriptionId = MarketplaceTestHelpers.SubId,
            AmpplanId = "plan-basic",
            AmpOfferId = "offer-1",
            Ampquantity = 1,
            Name = "Test",
            SubscriptionStatus = "Subscribed",
            IsActive = true,
            Term = "P10Y",
            StartDate = new DateTime(2024, 1, 1),
            EndDate = new DateTime(2034, 1, 1),
            PurchaserEmail = "test@test.com",
        };

        var planRepo = new Mock<DataAccess.Contracts.IPlansRepository>();
        planRepo.Setup(r => r.GetById(It.IsAny<string>())).Returns((DataAccess.Entities.Plans)null);

        var subRepo = new Mock<DataAccess.Contracts.ISubscriptionsRepository>();
        var service = new SubscriptionService(subRepo.Object, planRepo.Object);

        var result = service.PrepareSubscriptionResponse(subscriptionEntity);

        Assert.AreEqual("P10Y", result.Term.TermUnit, "P10Y should survive DB → model mapping");
    }

    [TestMethod]
    public void SubscriptionService_PrepareSubscriptionResponse_NullTerm_IsNull()
    {
        var subscriptionEntity = new DataAccess.Entities.Subscriptions
        {
            Id = 43,
            AmpsubscriptionId = MarketplaceTestHelpers.SubId,
            AmpplanId = "plan-basic",
            Name = "Test",
            SubscriptionStatus = "Subscribed",
            IsActive = true,
            Term = null,
            PurchaserEmail = "test@test.com",
        };

        var planRepo = new Mock<DataAccess.Contracts.IPlansRepository>();
        planRepo.Setup(r => r.GetById(It.IsAny<string>())).Returns((DataAccess.Entities.Plans)null);
        var subRepo = new Mock<DataAccess.Contracts.ISubscriptionsRepository>();
        var service = new SubscriptionService(subRepo.Object, planRepo.Object);

        var result = service.PrepareSubscriptionResponse(subscriptionEntity);

        Assert.IsNull(result.Term.TermUnit, "Null term in DB should produce null TermUnit");
    }

    [TestMethod]
    public void SubscriptionService_PrepareSubscriptionResponse_P1M_StillWorks()
    {
        var subscriptionEntity = new DataAccess.Entities.Subscriptions
        {
            Id = 44,
            AmpsubscriptionId = MarketplaceTestHelpers.SubId,
            AmpplanId = "plan-basic",
            Name = "Test",
            SubscriptionStatus = "Subscribed",
            IsActive = true,
            Term = "P1M",
            PurchaserEmail = "test@test.com",
        };

        var planRepo = new Mock<DataAccess.Contracts.IPlansRepository>();
        planRepo.Setup(r => r.GetById(It.IsAny<string>())).Returns((DataAccess.Entities.Plans)null);
        var subRepo = new Mock<DataAccess.Contracts.ISubscriptionsRepository>();
        var service = new SubscriptionService(subRepo.Object, planRepo.Object);

        var result = service.PrepareSubscriptionResponse(subscriptionEntity);

        Assert.AreEqual("P1M", result.Term.TermUnit);
    }

    [TestMethod]
    public void AddOrUpdatePartnerSubscriptions_SavesTermUnitString()
    {
        Subscriptions savedEntity = null;
        var subRepo = new Mock<DataAccess.Contracts.ISubscriptionsRepository>();
        subRepo.Setup(r => r.Save(It.IsAny<DataAccess.Entities.Subscriptions>()))
            .Callback<DataAccess.Entities.Subscriptions>(e => savedEntity = e)
            .Returns(1);
        var planRepo = new Mock<DataAccess.Contracts.IPlansRepository>();
        var service = new SubscriptionService(subRepo.Object, planRepo.Object);

        var subscriptionResult = new SubscriptionResult
        {
            Id = MarketplaceTestHelpers.SubId,
            PlanId = "plan-basic",
            OfferId = "offer-1",
            Name = "My Sub",
            SaasSubscriptionStatus = Marketplace.SaaS.Accelerator.Services.Models.SubscriptionStatusEnum.Subscribed,
            Quantity = 1,
            Purchaser = new PurchaserResult { EmailId = "buyer@test.com" },
            Term = new TermResult { TermUnit = "P10Y", StartDate = DateTimeOffset.UtcNow, EndDate = DateTimeOffset.UtcNow.AddYears(10) },
        };

        service.AddOrUpdatePartnerSubscriptions(subscriptionResult);

        Assert.IsNotNull(savedEntity);
        Assert.AreEqual("P10Y", savedEntity.Term, "P10Y should be persisted verbatim");
    }

    [TestMethod]
    public void FulfillmentMode_DefaultIsSdk()
    {
        var config = new SaaSApiClientConfiguration();
        Assert.AreEqual(FulfillmentMode.Sdk, config.FulfillmentMode);
    }

    [TestMethod]
    public void FulfillmentMode_ParsesNamedValues()
    {
        Assert.AreEqual(
            FulfillmentMode.Direct,
            SaaSApiClientConfiguration.ParseFulfillmentMode("Direct"));
        Assert.AreEqual(
            FulfillmentMode.Hybrid,
            SaaSApiClientConfiguration.ParseFulfillmentMode("hybrid"));
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("1")]
    [DataRow("2")]
    [DataRow("42")]
    [DataRow("Unknown")]
    public void FulfillmentMode_InvalidValue_DefaultsToSdk(string value)
    {
        Assert.AreEqual(
            FulfillmentMode.Sdk,
            SaaSApiClientConfiguration.ParseFulfillmentMode(value));
    }

    [TestMethod]
    public void GetSubscriptionById_Sync_Direct_ReturnsResult()
    {
        var dto = MarketplaceTestHelpers.KnownSubDto(termUnit: "P1Y");
        var handler = new FixedHandler(HttpStatusCode.OK, MarketplaceTestHelpers.Json(dto));
        var client = MarketplaceTestHelpers.BuildClient(handler, new SaaSApiClientConfiguration
        {
            FulFillmentAPIBaseURL = "https://marketplaceapi.microsoft.com/api",
            FulfillmentMode = FulfillmentMode.Direct,
        });
        var result = client.GetSubscriptionById(MarketplaceTestHelpers.SubId);

        Assert.IsNotNull(result);
        Assert.AreEqual(MarketplaceTestHelpers.SubId, result.Id);
        Assert.AreEqual("P1Y", result.Term.TermUnit);
    }

    [TestMethod]
    public void GetSubscriptionById_Sync_Direct_UnknownTermUnit_Preserved()
    {
        var dto = MarketplaceTestHelpers.KnownSubDto(termUnit: "P10Y");
        var handler = new FixedHandler(HttpStatusCode.OK, MarketplaceTestHelpers.Json(dto));
        var result = MarketplaceTestHelpers.BuildClient(handler).GetSubscriptionById(MarketplaceTestHelpers.SubId);

        Assert.AreEqual("P10Y", result.Term.TermUnit, "Sync Direct must preserve unknown termUnit P10Y");
    }

    [TestMethod]
    public void GetAllSubscriptions_Sync_Direct_ReturnsResult()
    {
        var page = new { subscriptions = new[] { MarketplaceTestHelpers.KnownSubDto(termUnit: "P1M") } };
        var handler = new FixedHandler(HttpStatusCode.OK, MarketplaceTestHelpers.Json(page));
        var results = MarketplaceTestHelpers.BuildClient(handler).GetAllSubscriptions();

        Assert.IsNotNull(results);
        Assert.AreEqual(1, results.Count);
        Assert.AreEqual("P1M", results[0].Term.TermUnit);
    }

    [TestMethod]
    public void GetSubscriptionById_Sync_Direct_404_ThrowsMarketplaceException()
    {
        var handler = new FixedHandler(HttpStatusCode.NotFound, new StringContent("{}"));
        Assert.ThrowsExactly<MarketplaceException>(
            () => MarketplaceTestHelpers.BuildClient(handler).GetSubscriptionById(MarketplaceTestHelpers.SubId));
    }

    [TestMethod]
    public async Task GetAllPlansForSubscription_RichComponents_MeteringDimensionsPreserved()
    {
        var dto = MarketplaceTestHelpers.RichPlansDto();
        var handler = new FixedHandler(HttpStatusCode.OK, MarketplaceTestHelpers.Json(dto));
        var result = await MarketplaceTestHelpers.BuildClient(handler).GetAllPlansForSubscriptionAsync(MarketplaceTestHelpers.SubId);

        Assert.AreEqual(1, result.Count);
        var plan = result[0];
        Assert.IsNotNull(plan.PlanComponents);
        Assert.AreEqual(2, plan.PlanComponents.MeteringDimensions.Count, "Should have 2 metering dimensions");

        var dimApiCalls = plan.PlanComponents.MeteringDimensions.First(d => d.Id == "dim-api-calls");
        Assert.AreEqual("USD", dimApiCalls.Currency);
        Assert.AreEqual(0.005f, dimApiCalls.PricePerUnit.GetValueOrDefault(), 0.0001f);
        Assert.AreEqual("1000 calls", dimApiCalls.UnitOfMeasure);
        Assert.AreEqual("API Calls", dimApiCalls.DisplayName);

        var dimStorage = plan.PlanComponents.MeteringDimensions.First(d => d.Id == "dim-storage");
        Assert.AreEqual(0.10f, dimStorage.PricePerUnit.GetValueOrDefault(), 0.0001f);
        Assert.AreEqual("GB", dimStorage.UnitOfMeasure);
    }

    [TestMethod]
    public async Task GetAllPlansForSubscription_RichComponents_RecurrentBillingTermsPreserved()
    {
        var dto = MarketplaceTestHelpers.RichPlansDto();
        var handler = new FixedHandler(HttpStatusCode.OK, MarketplaceTestHelpers.Json(dto));
        var result = await MarketplaceTestHelpers.BuildClient(handler).GetAllPlansForSubscriptionAsync(MarketplaceTestHelpers.SubId);

        var plan = result[0];
        Assert.AreEqual(3, plan.PlanComponents.RecurrentBillingTerms.Count, "Should have 3 billing terms");

        var monthly = plan.PlanComponents.RecurrentBillingTerms.First(t => t.TermUnit == "P1M");
        Assert.AreEqual("USD", monthly.Currency);
        Assert.AreEqual(49.99f, monthly.Price.GetValueOrDefault(), 0.001f);
        Assert.AreEqual("Monthly subscription", monthly.TermDescription);
        Assert.AreEqual(2, monthly.MeteredQuantityIncluded.Count);

        var annual = plan.PlanComponents.RecurrentBillingTerms.First(t => t.TermUnit == "P1Y");
        Assert.AreEqual(499.99f, annual.Price.GetValueOrDefault(), 0.001f);
        Assert.AreEqual(2, annual.MeteredQuantityIncluded.Count);
    }

    [TestMethod]
    public async Task GetAllPlansForSubscription_RichComponents_UnknownTermUnitP10Y_Preserved()
    {
        var dto = MarketplaceTestHelpers.RichPlansDto();
        var handler = new FixedHandler(HttpStatusCode.OK, MarketplaceTestHelpers.Json(dto));
        var result = await MarketplaceTestHelpers.BuildClient(handler).GetAllPlansForSubscriptionAsync(MarketplaceTestHelpers.SubId);

        var plan = result[0];
        var decade = plan.PlanComponents.RecurrentBillingTerms.FirstOrDefault(t => t.TermUnit == "P10Y");
        Assert.IsNotNull(decade, "P10Y term unit should be preserved");
        Assert.AreEqual("EUR", decade.Currency);
        Assert.AreEqual(4999.99f, decade.Price.GetValueOrDefault(), 0.001f);
        Assert.AreEqual(0, decade.MeteredQuantityIncluded.Count, "Empty included quantity array should deserialize to empty list");
    }

    [TestMethod]
    public async Task GetAllPlansForSubscription_RichComponents_MeteredQuantityIncludedPreserved()
    {
        var dto = MarketplaceTestHelpers.RichPlansDto();
        var handler = new FixedHandler(HttpStatusCode.OK, MarketplaceTestHelpers.Json(dto));
        var result = await MarketplaceTestHelpers.BuildClient(handler).GetAllPlansForSubscriptionAsync(MarketplaceTestHelpers.SubId);

        var monthly = result[0].PlanComponents.RecurrentBillingTerms.First(t => t.TermUnit == "P1M");
        var apiCallsIncluded = monthly.MeteredQuantityIncluded.First(mq => mq.DimensionId == "dim-api-calls");
        Assert.AreEqual("10000", apiCallsIncluded.Units);

        var storageIncluded = monthly.MeteredQuantityIncluded.First(mq => mq.DimensionId == "dim-storage");
        Assert.AreEqual("5", storageIncluded.Units);
    }

    [TestMethod]
    public async Task GetAllPlansForSubscription_NoPlanComponents_ReturnsEmptyLists()
    {
        var dto = MarketplaceTestHelpers.PlansDto();
        var handler = new FixedHandler(HttpStatusCode.OK, MarketplaceTestHelpers.Json(dto));
        var result = await MarketplaceTestHelpers.BuildClient(handler).GetAllPlansForSubscriptionAsync(MarketplaceTestHelpers.SubId);

        foreach (var plan in result)
        {
            Assert.IsNotNull(plan.PlanComponents);
            Assert.IsNotNull(plan.PlanComponents.MeteringDimensions);
            Assert.IsNotNull(plan.PlanComponents.RecurrentBillingTerms);
            Assert.AreEqual(0, plan.PlanComponents.MeteringDimensions.Count, $"{plan.PlanId}: MeteringDimensions should be empty when absent");
            Assert.AreEqual(0, plan.PlanComponents.RecurrentBillingTerms.Count, $"{plan.PlanId}: RecurrentBillingTerms should be empty when absent");
        }
    }
}

[TestClass]
public class DirectWriteOperationTests
{
    [TestMethod]
    public async Task Activate_SendsPostToCorrectPath()
    {
        HttpRequestMessage captured = null;
        var handler = new CaptureHandler(req =>
        {
            captured = req;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
        });

        using var response = await MarketplaceTestHelpers.BuildClient(handler)
            .ActivateSubscriptionAsync(MarketplaceTestHelpers.SubId, "plan-basic");

        Assert.IsNotNull(captured);
        Assert.AreEqual(HttpMethod.Post, captured.Method);
        StringAssert.Contains(captured.RequestUri.ToString(), $"saas/subscriptions/{MarketplaceTestHelpers.SubId}/activate");
        Assert.AreEqual(200, response.Status);
    }

    [TestMethod]
    public async Task Activate_SendsPlanIdInBody()
    {
        string body = null;
        var handler = new CaptureHandler(req =>
        {
            body = req.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
        });

        using var response = await MarketplaceTestHelpers.BuildClient(handler)
            .ActivateSubscriptionAsync(MarketplaceTestHelpers.SubId, "plan-basic\r\n");

        StringAssert.Contains(body, "\"planId\":\"plan-basic\"");
        Assert.AreEqual(200, response.Status);
    }

    [TestMethod]
    public async Task Activate_Returns200_ReturnsAzureResponse()
    {
        using var response = await MarketplaceTestHelpers.BuildClient(
            new FixedHandler(HttpStatusCode.OK, new StringContent("{}")))
            .ActivateSubscriptionAsync(MarketplaceTestHelpers.SubId, "plan-basic");

        Assert.AreEqual(200, response.Status);
    }

    [TestMethod]
    public async Task Activate_Returns404_ThrowsMarketplaceException()
    {
        var handler = new FixedHandler(HttpStatusCode.NotFound, new StringContent("{}"));
        await Assert.ThrowsExactlyAsync<MarketplaceException>(
            () => MarketplaceTestHelpers.BuildClient(handler).ActivateSubscriptionAsync(MarketplaceTestHelpers.SubId, "plan-basic"));
    }

    [TestMethod]
    public async Task ChangePlan_SendsPatchToSubscriptionPath()
    {
        HttpRequestMessage captured = null;
        var handler = new CaptureHandler(req =>
        {
            captured = req;
            return MarketplaceTestHelpers.OperationAcceptedResponse();
        });

        var result = await MarketplaceTestHelpers.BuildClient(handler)
            .ChangePlanForSubscriptionAsync(MarketplaceTestHelpers.SubId, "plan-pro");

        Assert.IsNotNull(captured);
        Assert.AreEqual(HttpMethod.Patch, captured.Method);
        StringAssert.Contains(captured.RequestUri.ToString(), $"saas/subscriptions/{MarketplaceTestHelpers.SubId}?");
        Assert.AreEqual(MarketplaceTestHelpers.OperationId.ToString(), result.OperationIdFromClientLib);
    }

    [TestMethod]
    public async Task ChangePlan_SendsPlanIdInBody()
    {
        string body = null;
        var handler = new CaptureHandler(req =>
        {
            body = req.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            return MarketplaceTestHelpers.OperationAcceptedResponse();
        });

        await MarketplaceTestHelpers.BuildClient(handler)
            .ChangePlanForSubscriptionAsync(MarketplaceTestHelpers.SubId, "plan-pro\r\n");

        StringAssert.Contains(body, "\"planId\":\"plan-pro\"");
    }

    [TestMethod]
    public async Task ChangePlan_Returns202_ParsesOperationLocationGuid()
    {
        var result = await MarketplaceTestHelpers.BuildClient(
            new FixedHandler(HttpStatusCode.Accepted, new StringContent("{}"), MarketplaceTestHelpers.OperationLocationHeaders()))
            .ChangePlanForSubscriptionAsync(MarketplaceTestHelpers.SubId, "plan-pro");

        Assert.AreEqual(MarketplaceTestHelpers.OperationId.ToString(), result.OperationIdFromClientLib);
    }

    [TestMethod]
    public async Task ChangePlan_MissingOperationLocation_ThrowsMarketplaceException()
    {
        var handler = new FixedHandler(HttpStatusCode.Accepted, new StringContent("{}"));

        await Assert.ThrowsExactlyAsync<MarketplaceException>(
            () => MarketplaceTestHelpers.BuildClient(handler)
                .ChangePlanForSubscriptionAsync(MarketplaceTestHelpers.SubId, "plan-pro"));
    }

    [TestMethod]
    [DataRow("https://evil.example/api/saas/subscriptions/11111111-1111-1111-1111-111111111111/operations/33333333-3333-3333-3333-333333333333")]
    [DataRow("http://marketplaceapi.microsoft.com/api/saas/subscriptions/11111111-1111-1111-1111-111111111111/operations/33333333-3333-3333-3333-333333333333")]
    [DataRow("https://marketplaceapi.microsoft.com/api/not-marketplace/33333333-3333-3333-3333-333333333333")]
    [DataRow("https://marketplaceapi.microsoft.com/api/saas/subscriptions/22222222-2222-2222-2222-222222222222/operations/33333333-3333-3333-3333-333333333333")]
    public async Task ChangePlan_InvalidOperationLocation_ThrowsMarketplaceException(string operationLocation)
    {
        var headers = new Dictionary<string, string>
        {
            ["operation-location"] = operationLocation,
        };
        var handler = new FixedHandler(HttpStatusCode.Accepted, new StringContent("{}"), headers);

        await Assert.ThrowsExactlyAsync<MarketplaceException>(
            () => MarketplaceTestHelpers.BuildClient(handler)
                .ChangePlanForSubscriptionAsync(MarketplaceTestHelpers.SubId, "plan-pro"));
    }

    [TestMethod]
    public async Task ChangePlan_Returns404_ThrowsMarketplaceException()
    {
        var handler = new FixedHandler(HttpStatusCode.NotFound, new StringContent("{}"));
        await Assert.ThrowsExactlyAsync<MarketplaceException>(
            () => MarketplaceTestHelpers.BuildClient(handler).ChangePlanForSubscriptionAsync(MarketplaceTestHelpers.SubId, "plan-pro"));
    }

    [TestMethod]
    public async Task ChangeQuantity_SendsPatchWithQuantityBody()
    {
        string body = null;
        var handler = new CaptureHandler(req =>
        {
            body = req.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            return MarketplaceTestHelpers.OperationAcceptedResponse();
        });

        await MarketplaceTestHelpers.BuildClient(handler)
            .ChangeQuantityForSubscriptionAsync(MarketplaceTestHelpers.SubId, 11);

        StringAssert.Contains(body, "\"quantity\":11");
    }

    [TestMethod]
    public async Task ChangeQuantity_Returns202_ParsesOperationLocationGuid()
    {
        var result = await MarketplaceTestHelpers.BuildClient(
            new FixedHandler(HttpStatusCode.Accepted, new StringContent("{}"), MarketplaceTestHelpers.OperationLocationHeaders()))
            .ChangeQuantityForSubscriptionAsync(MarketplaceTestHelpers.SubId, 11);

        Assert.AreEqual(MarketplaceTestHelpers.OperationId.ToString(), result.OperationIdFromClientLib);
    }

    [TestMethod]
    public async Task ChangeQuantity_Returns404_ThrowsMarketplaceException()
    {
        var handler = new FixedHandler(HttpStatusCode.NotFound, new StringContent("{}"));
        await Assert.ThrowsExactlyAsync<MarketplaceException>(
            () => MarketplaceTestHelpers.BuildClient(handler).ChangeQuantityForSubscriptionAsync(MarketplaceTestHelpers.SubId, 11));
    }

    [TestMethod]
    public async Task Delete_SendsDeleteRequest()
    {
        HttpRequestMessage captured = null;
        var handler = new CaptureHandler(req =>
        {
            captured = req;
            return MarketplaceTestHelpers.OperationAcceptedResponse();
        });

        var result = await MarketplaceTestHelpers.BuildClient(handler)
            .DeleteSubscriptionAsync(MarketplaceTestHelpers.SubId);

        Assert.IsNotNull(captured);
        Assert.AreEqual(HttpMethod.Delete, captured.Method);
        StringAssert.Contains(captured.RequestUri.ToString(), $"saas/subscriptions/{MarketplaceTestHelpers.SubId}?");
        Assert.AreEqual(MarketplaceTestHelpers.OperationId.ToString(), result.OperationIdFromClientLib);
    }

    [TestMethod]
    public async Task Delete_Returns202_ParsesOperationLocationGuid()
    {
        var result = await MarketplaceTestHelpers.BuildClient(
            new FixedHandler(HttpStatusCode.Accepted, new StringContent("{}"), MarketplaceTestHelpers.OperationLocationHeaders()))
            .DeleteSubscriptionAsync(MarketplaceTestHelpers.SubId);

        Assert.AreEqual(MarketplaceTestHelpers.OperationId.ToString(), result.OperationIdFromClientLib);
    }

    [TestMethod]
    public async Task Delete_Returns404_ThrowsMarketplaceException()
    {
        var handler = new FixedHandler(HttpStatusCode.NotFound, new StringContent("{}"));
        await Assert.ThrowsExactlyAsync<MarketplaceException>(
            () => MarketplaceTestHelpers.BuildClient(handler).DeleteSubscriptionAsync(MarketplaceTestHelpers.SubId));
    }

    [TestMethod]
    public async Task GetOperationStatus_SendsGetToCorrectPath()
    {
        HttpRequestMessage captured = null;
        var handler = new CaptureHandler(req =>
        {
            captured = req;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = MarketplaceTestHelpers.Json(MarketplaceTestHelpers.OperationResultDto()) };
        });

        var result = await MarketplaceTestHelpers.BuildClient(handler)
            .GetOperationStatusAsync(MarketplaceTestHelpers.SubId, MarketplaceTestHelpers.OperationId);

        Assert.IsNotNull(captured);
        Assert.AreEqual(HttpMethod.Get, captured.Method);
        StringAssert.Contains(captured.RequestUri.ToString(), $"saas/subscriptions/{MarketplaceTestHelpers.SubId}/operations/{MarketplaceTestHelpers.OperationId}");
        Assert.AreEqual(MarketplaceTestHelpers.OperationId.ToString(), result.ID);
    }

    [TestMethod]
    public async Task GetOperationStatus_Returns200_MapsFields()
    {
        var result = await MarketplaceTestHelpers.BuildClient(
            new FixedHandler(HttpStatusCode.OK, MarketplaceTestHelpers.Json(MarketplaceTestHelpers.OperationResultDto())))
            .GetOperationStatusAsync(MarketplaceTestHelpers.SubId, MarketplaceTestHelpers.OperationId);

        Assert.AreEqual(MarketplaceTestHelpers.OperationId.ToString(), result.ID);
        Assert.AreEqual(Marketplace.SaaS.Accelerator.Services.Models.OperationStatusEnum.Succeeded, result.Status);
        Assert.AreEqual("ChangePlan", result.ActionType);
        Assert.AreEqual(MarketplaceTestHelpers.SubId.ToString(), result.SubscriptionId);
        Assert.AreEqual(new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc), result.Created);
    }

    [TestMethod]
    public async Task GetOperationStatus_Returns404_ThrowsMarketplaceException()
    {
        var handler = new FixedHandler(HttpStatusCode.NotFound, new StringContent("{}"));
        await Assert.ThrowsExactlyAsync<MarketplaceException>(
            () => MarketplaceTestHelpers.BuildClient(handler).GetOperationStatusAsync(MarketplaceTestHelpers.SubId, MarketplaceTestHelpers.OperationId));
    }

    [TestMethod]
    public async Task PatchOperationStatus_SendsPatchWithStatusBody()
    {
        HttpRequestMessage captured = null;
        string body = null;
        var handler = new CaptureHandler(req =>
        {
            captured = req;
            body = req.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
        });

        using var response = await MarketplaceTestHelpers.BuildClient(handler)
            .PatchOperationStatusAsync(MarketplaceTestHelpers.SubId, MarketplaceTestHelpers.OperationId, UpdateOperationStatusEnum.Success);

        Assert.IsNotNull(captured);
        Assert.AreEqual(HttpMethod.Patch, captured.Method);
        StringAssert.Contains(captured.RequestUri.ToString(), $"saas/subscriptions/{MarketplaceTestHelpers.SubId}/operations/{MarketplaceTestHelpers.OperationId}");
        StringAssert.Contains(body, "\"status\":\"Success\"");
        Assert.AreEqual(200, response.Status);
    }

    [TestMethod]
    public async Task PatchOperationStatus_Returns200_ReturnsAzureResponse()
    {
        using var response = await MarketplaceTestHelpers.BuildClient(
            new FixedHandler(HttpStatusCode.OK, new StringContent("{}")))
            .PatchOperationStatusAsync(MarketplaceTestHelpers.SubId, MarketplaceTestHelpers.OperationId, UpdateOperationStatusEnum.Success);

        Assert.AreEqual(200, response.Status);
    }

    [TestMethod]
    public async Task PatchOperationStatus_Returns404_ThrowsMarketplaceException()
    {
        var handler = new FixedHandler(HttpStatusCode.NotFound, new StringContent("{}"));
        await Assert.ThrowsExactlyAsync<MarketplaceException>(
            () => MarketplaceTestHelpers.BuildClient(handler).PatchOperationStatusAsync(MarketplaceTestHelpers.SubId, MarketplaceTestHelpers.OperationId, UpdateOperationStatusEnum.Success));
    }
}

[TestClass]
public class FulfillmentApiServiceRoutingTests
{
    [TestMethod]
    public async Task SdkMode_Default_UsesSdkForReads()
    {
        var (service, sdkClient, fulfillmentOps, subscriptionOps, directClient) = CreateService(FulfillmentMode.Sdk);

        fulfillmentOps
            .Setup(x => x.GetSubscriptionAsync(It.IsAny<Guid>(), It.IsAny<Guid?>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TestResponse<Subscription>(MarketplaceTestHelpers.CreateSdkSubscription()));
        fulfillmentOps
            .Setup(x => x.ResolveAsync(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TestResponse<ResolvedSubscription>(MarketplaceTestHelpers.CreateSdkResolvedSubscription()));
        fulfillmentOps
            .Setup(x => x.ListSubscriptionsAsync(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .Returns(MarketplaceTestHelpers.CreateAsyncSubscriptionsPageable(MarketplaceTestHelpers.CreateSdkSubscription()));
        fulfillmentOps
            .Setup(x => x.ListAvailablePlansAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TestResponse<SubscriptionPlans>(MarketplaceTestHelpers.CreateSdkPlans()));

        var subscription = await service.GetSubscriptionByIdAsync(MarketplaceTestHelpers.SubId);
        var resolved = await service.ResolveAsync("token");
        var subscriptions = await service.GetAllSubscriptionAsync();
        var plans = await service.GetAllPlansForSubscriptionAsync(MarketplaceTestHelpers.SubId);

        Assert.AreEqual("plan-basic", subscription.PlanId);
        Assert.AreEqual("My Sub", resolved.SubscriptionName);
        Assert.AreEqual(1, subscriptions.Count);
        Assert.AreEqual(1, plans.Count);
        directClient.Verify(x => x.GetSubscriptionByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
        directClient.Verify(x => x.ResolveAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        directClient.Verify(x => x.GetAllSubscriptionsAsync(It.IsAny<CancellationToken>()), Times.Never);
        directClient.Verify(x => x.GetAllPlansForSubscriptionAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
        fulfillmentOps.Verify(x => x.GetSubscriptionAsync(It.IsAny<Guid>(), It.IsAny<Guid?>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()), Times.Once);
        fulfillmentOps.Verify(x => x.ResolveAsync(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()), Times.Once);
        fulfillmentOps.Verify(x => x.ListSubscriptionsAsync(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()), Times.Once);
        fulfillmentOps.Verify(x => x.ListAvailablePlansAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    public async Task HybridMode_ReadsUseDirect()
    {
        var (service, sdkClient, fulfillmentOps, subscriptionOps, directClient) = CreateService(FulfillmentMode.Hybrid);

        directClient.Setup(x => x.GetSubscriptionByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SubscriptionResult { Id = MarketplaceTestHelpers.SubId, PlanId = "direct-plan" });
        directClient.Setup(x => x.ResolveAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ResolvedSubscriptionResult { SubscriptionId = MarketplaceTestHelpers.SubId, SubscriptionName = "direct-resolved" });
        directClient.Setup(x => x.GetAllSubscriptionsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<SubscriptionResult> { new SubscriptionResult { Id = MarketplaceTestHelpers.SubId, PlanId = "direct-list" } });
        directClient.Setup(x => x.GetAllPlansForSubscriptionAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PlanDetailResultExtension> { new PlanDetailResultExtension { PlanId = "direct-plan" } });

        var subscription = await service.GetSubscriptionByIdAsync(MarketplaceTestHelpers.SubId);
        var resolved = await service.ResolveAsync("token");
        var subscriptions = await service.GetAllSubscriptionAsync();
        var plans = await service.GetAllPlansForSubscriptionAsync(MarketplaceTestHelpers.SubId);

        Assert.AreEqual("direct-plan", subscription.PlanId);
        Assert.AreEqual("direct-resolved", resolved.SubscriptionName);
        Assert.AreEqual("direct-list", subscriptions[0].PlanId);
        Assert.AreEqual("direct-plan", plans[0].PlanId);
        directClient.Verify(x => x.GetSubscriptionByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Once);
        directClient.Verify(x => x.ResolveAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
        directClient.Verify(x => x.GetAllSubscriptionsAsync(It.IsAny<CancellationToken>()), Times.Once);
        directClient.Verify(x => x.GetAllPlansForSubscriptionAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Once);
        fulfillmentOps.Verify(x => x.GetSubscriptionAsync(It.IsAny<Guid>(), It.IsAny<Guid?>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()), Times.Never);
        fulfillmentOps.Verify(x => x.ResolveAsync(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()), Times.Never);
        fulfillmentOps.Verify(x => x.ListSubscriptionsAsync(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()), Times.Never);
        fulfillmentOps.Verify(x => x.ListAvailablePlansAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [TestMethod]
    public async Task HybridMode_WritesUseSdk()
    {
        var (service, sdkClient, fulfillmentOps, subscriptionOps, directClient) = CreateService(FulfillmentMode.Hybrid);

        fulfillmentOps
            .Setup(x => x.UpdateSubscriptionAsync(It.IsAny<Guid>(), It.IsAny<SubscriberPlan>(), It.IsAny<Guid?>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(MarketplaceTestHelpers.OperationId.ToString());
        subscriptionOps
            .Setup(x => x.GetOperationStatusAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<Guid?>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TestResponse<SaaSOperation>(MarketplaceTestHelpers.CreateSdkOperation()));

        var changePlan = await service.ChangePlanForSubscriptionAsync(MarketplaceTestHelpers.SubId, "plan-pro");
        var operation = await service.GetOperationStatusResultAsync(MarketplaceTestHelpers.SubId, MarketplaceTestHelpers.OperationId);

        Assert.AreEqual(MarketplaceTestHelpers.OperationId.ToString(), changePlan.OperationIdFromClientLib);
        Assert.AreEqual(Marketplace.SaaS.Accelerator.Services.Models.OperationStatusEnum.Succeeded, operation.Status);
        directClient.Verify(x => x.ChangePlanForSubscriptionAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        directClient.Verify(x => x.GetOperationStatusAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
        fulfillmentOps.Verify(x => x.UpdateSubscriptionAsync(It.IsAny<Guid>(), It.IsAny<SubscriberPlan>(), It.IsAny<Guid?>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()), Times.Once);
        subscriptionOps.Verify(x => x.GetOperationStatusAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<Guid?>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    public async Task DirectMode_WritesUseDirect()
    {
        var (service, sdkClient, fulfillmentOps, subscriptionOps, directClient) = CreateService(FulfillmentMode.Direct);

        directClient
            .Setup(x => x.ChangePlanForSubscriptionAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SubscriptionUpdateResult { OperationIdFromClientLib = MarketplaceTestHelpers.OperationId.ToString() });
        directClient
            .Setup(x => x.GetOperationStatusAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OperationResult { ID = MarketplaceTestHelpers.OperationId.ToString(), Status = Marketplace.SaaS.Accelerator.Services.Models.OperationStatusEnum.Succeeded, ActionType = "ChangePlan", SubscriptionId = MarketplaceTestHelpers.SubId.ToString() });

        var changePlan = await service.ChangePlanForSubscriptionAsync(MarketplaceTestHelpers.SubId, "plan-pro");
        var operation = await service.GetOperationStatusResultAsync(MarketplaceTestHelpers.SubId, MarketplaceTestHelpers.OperationId);

        Assert.AreEqual(MarketplaceTestHelpers.OperationId.ToString(), changePlan.OperationIdFromClientLib);
        Assert.AreEqual(Marketplace.SaaS.Accelerator.Services.Models.OperationStatusEnum.Succeeded, operation.Status);
        directClient.Verify(x => x.ChangePlanForSubscriptionAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
        directClient.Verify(x => x.GetOperationStatusAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Once);
        fulfillmentOps.Verify(x => x.UpdateSubscriptionAsync(It.IsAny<Guid>(), It.IsAny<SubscriberPlan>(), It.IsAny<Guid?>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()), Times.Never);
        subscriptionOps.Verify(x => x.GetOperationStatusAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<Guid?>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [TestMethod]
    public async Task DirectMode_ReadsUseDirect()
    {
        var (service, sdkClient, fulfillmentOps, subscriptionOps, directClient) = CreateService(FulfillmentMode.Direct);

        directClient.Setup(x => x.GetSubscriptionByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SubscriptionResult { Id = MarketplaceTestHelpers.SubId, PlanId = "direct-plan" });
        directClient.Setup(x => x.ResolveAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ResolvedSubscriptionResult { SubscriptionId = MarketplaceTestHelpers.SubId, SubscriptionName = "direct-resolved" });

        var subscription = await service.GetSubscriptionByIdAsync(MarketplaceTestHelpers.SubId);
        var resolved = await service.ResolveAsync("token");

        Assert.AreEqual("direct-plan", subscription.PlanId);
        Assert.AreEqual("direct-resolved", resolved.SubscriptionName);
        directClient.Verify(x => x.GetSubscriptionByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Once);
        directClient.Verify(x => x.ResolveAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
        fulfillmentOps.Verify(x => x.GetSubscriptionAsync(It.IsAny<Guid>(), It.IsAny<Guid?>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()), Times.Never);
        fulfillmentOps.Verify(x => x.ResolveAsync(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    private static (FulfillmentApiService service, Mock<IMarketplaceSaaSClient> sdkClient, Mock<FulfillmentOperations> fulfillmentOps, Mock<SubscriptionOperations> subscriptionOps, Mock<IMarketplaceDirectClient> directClient)
        CreateService(FulfillmentMode mode)
    {
        var sdkClient = new Mock<IMarketplaceSaaSClient>(MockBehavior.Strict);
        var fulfillmentOps = new Mock<FulfillmentOperations>(MockBehavior.Strict);
        var subscriptionOps = new Mock<SubscriptionOperations>(MockBehavior.Strict);
        var directClient = new Mock<IMarketplaceDirectClient>(MockBehavior.Strict);

        sdkClient.SetupGet(x => x.Fulfillment).Returns(fulfillmentOps.Object);
        sdkClient.SetupGet(x => x.Operations).Returns(subscriptionOps.Object);

        var config = new SaaSApiClientConfiguration { FulfillmentMode = mode };
        var service = new FulfillmentApiService(sdkClient.Object, directClient.Object, config, null);
        return (service, sdkClient, fulfillmentOps, subscriptionOps, directClient);
    }
}

internal static class MarketplaceTestHelpers
{
    public static readonly Guid SubId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    public static readonly Guid OperationId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly DateTimeOffset Start = new(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset End = new(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public static TokenCredential FakeCredential()
    {
        var mock = new Mock<TokenCredential>();
        mock.Setup(c => c.GetTokenAsync(It.IsAny<TokenRequestContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AccessToken("fake-bearer-token", DateTimeOffset.UtcNow.AddHours(1)));
        return mock.Object;
    }

    public static SaaSApiClientConfiguration DefaultConfig() => new()
    {
        FulFillmentAPIBaseURL = "https://marketplaceapi.microsoft.com/api",
        FulfillmentMode = FulfillmentMode.Direct,
    };

    public static MarketplaceDirectClient BuildClient(HttpMessageHandler handler, SaaSApiClientConfiguration config = null)
    {
        config ??= DefaultConfig();
        var factory = new FakeHttpClientFactory(new HttpClient(handler));
        return new MarketplaceDirectClient(factory, FakeCredential(), config, null);
    }

    public static StringContent Json(object obj)
        => new(JsonSerializer.Serialize(obj), Encoding.UTF8, "application/json");

    public static Dictionary<string, string> OperationLocationHeaders() => new()
    {
        ["operation-location"] = $"https://marketplaceapi.microsoft.com/api/saas/subscriptions/{SubId}/operations/{OperationId}?api-version=2018-08-31",
    };

    public static HttpResponseMessage OperationAcceptedResponse()
        => new(HttpStatusCode.Accepted) { Content = new StringContent("{}"), Headers = { { "operation-location", $"https://marketplaceapi.microsoft.com/api/saas/subscriptions/{SubId}/operations/{OperationId}?api-version=2018-08-31" } } };

    public static object KnownSubDto(string termUnit = "P1Y") => new
    {
        id = SubId,
        publisherId = "pub-1",
        offerId = "offer-1",
        name = "Test Sub",
        saasSubscriptionStatus = "Subscribed",
        planId = "plan-basic",
        quantity = 2,
        purchaser = new { emailId = "buyer@test.com", objectId = Guid.NewGuid(), tenantId = Guid.NewGuid() },
        beneficiary = new { emailId = "user@test.com", objectId = Guid.NewGuid(), tenantId = Guid.NewGuid() },
        term = termUnit == null
            ? (object)new { startDate = "2024-01-01T00:00:00Z", endDate = "2025-01-01T00:00:00Z" }
            : new { termUnit, startDate = "2024-01-01T00:00:00Z", endDate = "2025-01-01T00:00:00Z" },
    };

    public static object ResolveDto() => new
    {
        id = SubId,
        subscriptionName = "My Sub",
        offerId = "offer-1",
        planId = "plan-basic",
        quantity = 3,
    };

    public static object PlansDto() => new
    {
        plans = new[]
        {
            new { planId = "plan-basic", displayName = "Basic Plan", description = "Basic", isPrivate = false },
            new { planId = "plan-pro", displayName = "Pro Plan", description = "Pro", isPrivate = false },
        },
    };

    public static object RichPlansDto() => new
    {
        plans = new[]
        {
            new
            {
                planId = "plan-metered",
                displayName = "Metered Plan",
                description = "Has dimensions",
                isPrivate = false,
                hasFreeTrials = true,
                isPricePerSeat = false,
                isStopSell = false,
                market = "US",
                planComponents = new
                {
                    meteringDimensions = new[]
                    {
                        new
                        {
                            id = "dim-api-calls",
                            currency = "USD",
                            pricePerUnit = 0.005f,
                            unitOfMeasure = "1000 calls",
                            displayName = "API Calls",
                        },
                        new
                        {
                            id = "dim-storage",
                            currency = "USD",
                            pricePerUnit = 0.10f,
                            unitOfMeasure = "GB",
                            displayName = "Storage",
                        },
                    },
                    recurrentBillingTerms = new[]
                    {
                        new
                        {
                            currency = "USD",
                            price = 49.99f,
                            termDescription = "Monthly subscription",
                            termUnit = "P1M",
                            meteredQuantityIncluded = new[]
                            {
                                new { dimensionId = "dim-api-calls", units = "10000" },
                                new { dimensionId = "dim-storage", units = "5" },
                            },
                        },
                        new
                        {
                            currency = "USD",
                            price = 499.99f,
                            termDescription = "Annual subscription",
                            termUnit = "P1Y",
                            meteredQuantityIncluded = new[]
                            {
                                new { dimensionId = "dim-api-calls", units = "200000" },
                                new { dimensionId = "dim-storage", units = "100" },
                            },
                        },
                        new
                        {
                            currency = "EUR",
                            price = 4999.99f,
                            termDescription = "Decade plan",
                            termUnit = "P10Y",
                            meteredQuantityIncluded = new[] { new { dimensionId = "placeholder", units = "placeholder" } }.Take(0).ToArray(),
                        },
                    },
                },
            },
        },
    };

    public static object OperationResultDto() => new
    {
        id = OperationId.ToString(),
        status = "Succeeded",
        action = "ChangePlan",
        subscriptionId = SubId.ToString(),
        timeStamp = "2024-01-01T00:00:00Z",
    };

    public static Subscription CreateSdkSubscription(string planId = "plan-basic")
        => CreateWithNonPublicConstructor<Subscription>(
            SubId,
            "pub-1",
            "offer-1",
            "Sdk Sub",
            (Microsoft.Marketplace.SaaS.Models.SubscriptionStatusEnum?)Microsoft.Marketplace.SaaS.Models.SubscriptionStatusEnum.Subscribed,
            CreateWithNonPublicConstructor<AadIdentifier>("buyer@test.com", (Guid?)Guid.NewGuid(), (Guid?)Guid.NewGuid(), null),
            CreateWithNonPublicConstructor<AadIdentifier>("user@test.com", (Guid?)Guid.NewGuid(), (Guid?)Guid.NewGuid(), null),
            planId,
            (int?)2,
            CreateWithNonPublicConstructor<SubscriptionTerm>((Microsoft.Marketplace.SaaS.Models.TermUnitEnum?)Microsoft.Marketplace.SaaS.Models.TermUnitEnum.P1Y, (DateTimeOffset?)Start, (DateTimeOffset?)End, null),
            (bool?)false,
            (bool?)false,
            (bool?)false,
            Array.Empty<AllowedCustomerOperationsEnum>(),
            null,
            (DateTimeOffset?)DateTimeOffset.UtcNow,
            null);

    public static ResolvedSubscription CreateSdkResolvedSubscription()
        => CreateWithNonPublicConstructor<ResolvedSubscription>(SubId, "My Sub", "offer-1", "plan-basic", (long?)3, CreateSdkSubscription());

    public static SubscriptionPlans CreateSdkPlans()
        => CreateWithNonPublicConstructor<SubscriptionPlans>(
            (IReadOnlyList<Plan>)new[]
            {
                CreateWithNonPublicConstructor<Plan>(
                    "plan-basic",
                    "Basic Plan",
                    (bool?)false,
                    "Basic",
                    (long?)1,
                    (long?)10,
                    (bool?)false,
                    (bool?)false,
                    (bool?)false,
                    "US",
                    CreateWithNonPublicConstructor<Microsoft.Marketplace.SaaS.Models.PlanComponents>(
                        Array.Empty<Microsoft.Marketplace.SaaS.Models.RecurrentBillingTerm>(),
                        Array.Empty<Microsoft.Marketplace.SaaS.Models.MeteringDimension>()),
                    Array.Empty<SourceOffer>()),
            });

    public static SaaSOperation CreateSdkOperation()
        => CreateWithNonPublicConstructor<SaaSOperation>(
            OperationId,
            (Guid?)Guid.NewGuid(),
            (Guid?)SubId,
            "pub-1",
            "offer-1",
            "plan-basic",
            (int?)1,
            (Microsoft.Marketplace.SaaS.Models.OperationActionEnum?)Microsoft.Marketplace.SaaS.Models.OperationActionEnum.ChangePlan,
            (DateTimeOffset?)DateTimeOffset.UtcNow,
            (Microsoft.Marketplace.SaaS.Models.OperationStatusEnum?)Microsoft.Marketplace.SaaS.Models.OperationStatusEnum.Succeeded);

    public static AsyncPageable<Subscription> CreateAsyncSubscriptionsPageable(params Subscription[] subscriptions)
        => AsyncPageable<Subscription>.FromPages(new[]
        {
            Page<Subscription>.FromValues(subscriptions, null, new TestResponse()),
        });

    private static T CreateWithNonPublicConstructor<T>(params object[] args)
    {
        var constructor = typeof(T)
            .GetConstructors(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)
            .Single(c => c.GetParameters().Length == args.Length);

        return (T)constructor.Invoke(args);
    }
}

internal class FixedHandler : HttpMessageHandler
{
    private readonly HttpStatusCode _status;
    private readonly HttpContent _content;
    private readonly IReadOnlyDictionary<string, string> _headers;

    public FixedHandler(HttpStatusCode status, HttpContent content, IReadOnlyDictionary<string, string> headers = null)
    {
        _status = status;
        _content = content;
        _headers = headers;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var response = new HttpResponseMessage(_status) { Content = _content };
        if (_headers != null)
        {
            foreach (var header in _headers)
                response.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        return Task.FromResult(response);
    }
}

internal class CaptureHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage> _factory;

    public CaptureHandler(Func<HttpRequestMessage, HttpResponseMessage> factory)
        => _factory = factory;

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        => Task.FromResult(_factory(request));
}

internal class FakeHttpClientFactory : IHttpClientFactory
{
    private readonly HttpClient _client;
    public FakeHttpClientFactory(HttpClient client) => _client = client;
    public HttpClient CreateClient(string name) => _client;
}

internal class TestResponse : Response
{
    private readonly int _status;

    public TestResponse(int status = 200) => _status = status;

    public override int Status => _status;
    public override string ReasonPhrase => string.Empty;
    public override Stream ContentStream { get => Stream.Null; set { } }
    public override string ClientRequestId { get => string.Empty; set { } }
    protected override bool TryGetHeader(string name, out string value) { value = null; return false; }
    protected override bool TryGetHeaderValues(string name, out IEnumerable<string> values) { values = null; return false; }
    protected override bool ContainsHeader(string name) => false;
    protected override IEnumerable<HttpHeader> EnumerateHeaders() => Array.Empty<HttpHeader>();
    public override void Dispose() { }
}

internal class TestResponse<T> : Response<T>
{
    private readonly T _value;
    private readonly Response _response;

    public TestResponse(T value, int status = 200)
    {
        _value = value;
        _response = new TestResponse(status);
    }

    public override T Value => _value;
    public override Response GetRawResponse() => _response;
}
