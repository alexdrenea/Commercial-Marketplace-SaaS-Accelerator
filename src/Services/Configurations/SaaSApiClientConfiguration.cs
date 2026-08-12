// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See LICENSE file in the project root for license information.
using System;
using Marketplace.SaaS.Accelerator.Services.Models;

namespace Marketplace.SaaS.Accelerator.Services.Configurations;

/// <summary>
/// Fulfillment Client Configuration.
/// </summary>
public class SaaSApiClientConfiguration
{
    /// <summary>
    /// Parses a named fulfillment mode, defaulting to SDK for missing or invalid values.
    /// Numeric enum representations are intentionally rejected.
    /// </summary>
    public static FulfillmentMode ParseFulfillmentMode(string value)
    {
        foreach (var name in Enum.GetNames<FulfillmentMode>())
        {
            if (string.Equals(name, value, StringComparison.OrdinalIgnoreCase))
                return Enum.Parse<FulfillmentMode>(name);
        }

        return FulfillmentMode.Sdk;
    }

    /// <summary>
    /// Gets or sets the type of the grant.
    /// </summary>
    /// <value>
    /// The type of the grant.
    /// </value>
    public string GrantType { get; set; }

    /// <summary>
    /// Gets or sets the client identifier.
    /// </summary>
    /// <value>
    /// The client identifier.
    /// </value>
    public string ClientId { get; set; }

    /// <summary>
    /// Gets or sets the client secret.
    /// </summary>
    /// <value>
    /// The client secret.
    /// </value>
    public string ClientSecret { get; set; }

    /// <summary>
    /// Gets or sets the AAD Client ID resource.
    /// </summary>
    /// <value>
    /// The resource.
    /// </value>
    public string MTClientId { get; set; }

    /// <summary>
    /// Gets or sets the resource.
    /// </summary>
    /// <value>
    /// The resource.
    /// </value>

    public string Resource { get; set; }

    /// <summary>
    /// Gets or sets the base URL.
    /// </summary>
    /// <value>
    /// The base URL.
    /// </value>
    public string FulFillmentAPIBaseURL { get; set; }

    /// <summary>
    /// Gets or sets the signed out redirect URI.
    /// </summary>
    /// <value>
    /// The signed out redirect URI.
    /// </value>
    public string SignedOutRedirectUri { get; set; }

    /// <summary>
    /// Gets or sets the tenant identifier.
    /// </summary>
    /// <value>
    /// The tenant identifier.
    /// </value>
    public string TenantId { get; set; }

    /// <summary>
    /// Gets or sets the API version.
    /// </summary>
    /// <value>
    /// The API version.
    /// </value>
    public string FulFillmentAPIVersion { get; set; }

    /// <summary>
    /// Gets or sets the Authentication end point.
    /// </summary>
    /// <value>
    /// The Authentication end point.
    /// </value>
    public string AdAuthenticationEndPoint { get; set; }

    /// <summary>
    /// Gets or sets the saa s application URL.
    /// </summary>
    /// <value>
    /// The saas application URL.
    /// </value>
    public string SaaSAppUrl { get; set; }

    /// <summary>
    /// Initializes or Gets the current run environment. Set to "development" or "production" is assumed.
    /// </summary>
    /// <value>
    /// The production-level environment. Typically, "development", "production", or null.
    /// </value>
    public string Environment { get; init; }
    /// <summary>
    /// Initializes or Gets the value for IsAdminPortalMultiTenant. Set to true or false is assumed.
    /// </summary>
    /// <value>
    /// The value for IsAdminPortalMultiTenant. Typically, true, false, or null.
    /// </value>
    public string IsAdminPortalMultiTenant { get; set; }

    /// <summary>
    /// Controls which HTTP transport is used for Marketplace Fulfillment operations.
    /// Accepted values: Sdk (default), Hybrid, Direct.
    /// Set via environment variable: SaaSApiConfiguration__FulfillmentMode
    /// Requires process restart to change; hot reload is not supported.
    /// </summary>
    public FulfillmentMode FulfillmentMode { get; set; } = FulfillmentMode.Sdk;


}