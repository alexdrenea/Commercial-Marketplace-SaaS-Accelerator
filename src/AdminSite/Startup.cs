// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See LICENSE file in the project root for license information.

using System;
using System.Diagnostics;
using System.Reflection;
using Azure.Identity;
using Marketplace.SaaS.Accelerator.AdminSite.Controllers;
using Marketplace.SaaS.Accelerator.DataAccess.Context;
using Marketplace.SaaS.Accelerator.DataAccess.Contracts;
using Marketplace.SaaS.Accelerator.DataAccess.Services;
using Marketplace.SaaS.Accelerator.Services.Configurations;
using Marketplace.SaaS.Accelerator.Services.Contracts;
using Marketplace.SaaS.Accelerator.Services.Models;
using Marketplace.SaaS.Accelerator.Services.Services;
using Marketplace.SaaS.Accelerator.Services.Utilities;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.Marketplace.Metering;
using Microsoft.Marketplace.SaaS;

namespace Marketplace.SaaS.Accelerator.AdminSite;

/// <summary>
/// Startup.
/// </summary>
public class Startup
{
    /// <summary>
    /// Initializes a new instance of the <see cref="Startup"/> class.
    /// </summary>
    /// <param name="configuration">The configuration.</param>
    public Startup(IConfiguration configuration)
    {
        this.Configuration = configuration;
    }

    /// <summary>
    /// Gets the configuration.
    /// </summary>
    /// <value>
    /// The configuration.
    /// </value>
    public IConfiguration Configuration { get; }

    /// <summary>
    /// Configures the services.
    /// </summary>
    /// <param name="services">The services.</param>
    public void ConfigureServices(IServiceCollection services)
    {
		services.Configure<ForwardedHeadersOptions>(options =>
		{
			options.ForwardedHeaders =
				Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedFor |
				Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedProto |
				Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedHost;
			options.ForwardLimit = 2;

			// CRITICAL: Clear these or .NET will reject headers from proxies not in the allow-list
			options.KnownIPNetworks.Clear();
			options.KnownProxies.Clear();
		});

		services.Configure<CookiePolicyOptions>(options =>
        {
            // This lambda determines whether user consent for non-essential cookies is needed for a given request.
            options.CheckConsentNeeded = context => true;
            options.MinimumSameSitePolicy = SameSiteMode.None;
        });

        var config = new SaaSApiClientConfiguration()
        {
            AdAuthenticationEndPoint = this.Configuration["SaaSApiConfiguration:AdAuthenticationEndPoint"],
            ClientId = this.Configuration["SaaSApiConfiguration:ClientId"] ?? Guid.Empty.ToString(),
            ClientSecret = this.Configuration["SaaSApiConfiguration:ClientSecret"] ?? String.Empty,
            FulFillmentAPIBaseURL = this.Configuration["SaaSApiConfiguration:FulFillmentAPIBaseURL"],
            MTClientId = this.Configuration["SaaSApiConfiguration:MTClientId"] ?? Guid.Empty.ToString(),
            FulFillmentAPIVersion = this.Configuration["SaaSApiConfiguration:FulFillmentAPIVersion"],
            GrantType = this.Configuration["SaaSApiConfiguration:GrantType"],
            Resource = this.Configuration["SaaSApiConfiguration:Resource"],
            SaaSAppUrl = this.Configuration["SaaSApiConfiguration:SaaSAppUrl"],
            SignedOutRedirectUri = this.Configuration["SaaSApiConfiguration:SignedOutRedirectUri"],
            TenantId = this.Configuration["SaaSApiConfiguration:TenantId"] ?? Guid.Empty.ToString(),
            IsAdminPortalMultiTenant = this.Configuration["SaaSApiConfiguration:IsAdminPortalMultiTenant"],
            FulfillmentMode = SaaSApiClientConfiguration.ParseFulfillmentMode(
                this.Configuration["SaaSApiConfiguration:FulfillmentMode"]),
        };
        var knownUsers = new KnownUsersModel()
        {
            KnownUsers = this.Configuration["KnownUsers"],
        };
        var creds = new ClientSecretCredential(config.TenantId.ToString(), config.ClientId.ToString(), config.ClientSecret);
        var boolMultiTenant = config.IsAdminPortalMultiTenant?.ToLower().Trim() ?? "false";



        services
            .AddAuthentication(options =>
            {
                options.DefaultAuthenticateScheme = OpenIdConnectDefaults.AuthenticationScheme;
                options.DefaultSignInScheme = CookieAuthenticationDefaults.AuthenticationScheme;
                options.DefaultChallengeScheme = CookieAuthenticationDefaults.AuthenticationScheme;
            })
            .AddOpenIdConnect(options =>
            {

                if (boolMultiTenant == "false")
                {
                    options.Authority = $"{config.AdAuthenticationEndPoint}/{config.TenantId}/v2.0";
                }
                else
                {
                    options.Authority = $"{config.AdAuthenticationEndPoint}/common/v2.0";
                }
                options.ClientId = config.MTClientId;
                options.ResponseType = OpenIdConnectResponseType.IdToken;
                options.CallbackPath = "/Home/Index";
                options.SignedOutRedirectUri = config.SignedOutRedirectUri;
                options.TokenValidationParameters.NameClaimType = ClaimConstants.CLAIM_SHORT_NAME;
                options.TokenValidationParameters.ValidateIssuer = false;
            })
            .AddCookie(options =>
            {
                options.ExpireTimeSpan = TimeSpan.FromMinutes(60);
                options.Cookie.MaxAge = options.ExpireTimeSpan;
                options.SlidingExpiration = true;
            });

        services
            .AddTransient<IClaimsTransformation, CustomClaimsTransformation>()
            .AddScoped<ExceptionHandlerAttribute>()
            .AddScoped<RequestLoggerActionFilter>()
        ;

        if (!Uri.TryCreate(config.FulFillmentAPIBaseURL, UriKind.Absolute, out var fulfillmentBaseApi))
        {
            fulfillmentBaseApi = new Uri("https://marketplaceapi.microsoft.com/api");
        }

        services.AddHttpClient(nameof(Marketplace.SaaS.Accelerator.Services.Services.MarketplaceDirectClient));

        services
            .AddSingleton<Azure.Core.TokenCredential>(creds)
            .AddSingleton<Marketplace.SaaS.Accelerator.Services.Contracts.IMarketplaceDirectClient>(sp =>
                new Marketplace.SaaS.Accelerator.Services.Services.MarketplaceDirectClient(
                    sp.GetRequiredService<System.Net.Http.IHttpClientFactory>(),
                    sp.GetRequiredService<Azure.Core.TokenCredential>(),
                    config,
                    new Marketplace.SaaS.Accelerator.Services.Utilities.FulfillmentApiClientLogger()))
            .AddSingleton<IFulfillmentApiService>(sp =>
                new FulfillmentApiService(
                    new MarketplaceSaaSClient(fulfillmentBaseApi, creds),
                    sp.GetRequiredService<Marketplace.SaaS.Accelerator.Services.Contracts.IMarketplaceDirectClient>(),
                    config,
                    new FulfillmentApiClientLogger()))
            .AddSingleton<IMeteredBillingApiService>(new MeteredBillingApiService(new MarketplaceMeteringClient(creds), config, new SaaSClientLogger<MeteredBillingApiService>()))
            .AddSingleton<SaaSApiClientConfiguration>(config)
            .AddSingleton<KnownUsersModel>(knownUsers);

        // Add the assembly version
        services.AddSingleton<IAppVersionService>(new AppVersionService(Assembly.GetExecutingAssembly()?.GetName()?.Version));

        services
            .AddScoped<ApplicationConfigService>();

        services
            .AddDbContext<SaasKitContext>(options => options.UseSqlServer(this.Configuration.GetConnectionString("DefaultConnection")));


        InitializeRepositoryServices(services);

        services.AddDistributedMemoryCache();
        services.AddSession(options =>
        {
            options.IdleTimeout = TimeSpan.FromMinutes(5);
            options.Cookie.HttpOnly = true;
            options.Cookie.IsEssential = true;
        });

        services.AddMvc(option => {
            option.EnableEndpointRouting = false;
            option.Filters.Add(new AutoValidateAntiforgeryTokenAttribute());
        });
        services.AddControllersWithViews();

        services.Configure<CookieTempDataProviderOptions>(options =>
        {
            options.Cookie.IsEssential = true;
        });

        services.AddScoped<OffersService>();
    }

    /// <summary>
    /// Configures the specified application.
    /// </summary>
    /// <param name="app">The application.</param>
    /// <param name="env">The env.</param>
    public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
    {
		app.UseForwardedHeaders();

		if (env.IsDevelopment())
        {
            app.UseDeveloperExceptionPage();
        }
        else
        {
            app.UseExceptionHandler("/Home/Error");
            app.UseHsts();
        }

        app.UseHttpsRedirection();
        app.UseStaticFiles();
        app.UseCookiePolicy();
        app.UseSession();
        app.UseAuthentication();
        app.UseMvc(routes =>
        {
            routes.MapRoute(
                name: "default",
                template: "{controller=Home}/{action=Index}/{id?}");
        });
    }

    /// <summary>
    /// Initializes the repository services.
    /// </summary>
    /// <param name="services">The services.</param>
    private static void InitializeRepositoryServices(IServiceCollection services)
    {
        services.AddScoped<ISubscriptionsRepository, SubscriptionsRepository>();
        services.AddScoped<IPlansRepository, PlansRepository>();
        services.AddScoped<IUsersRepository, UsersRepository>();
        services.AddScoped<ISubscriptionLogRepository, SubscriptionLogRepository>();
        services.AddScoped<IApplicationConfigRepository, ApplicationConfigRepository>();
        services.AddScoped<IApplicationLogRepository, ApplicationLogRepository>();
        services.AddScoped<ISubscriptionUsageLogsRepository, SubscriptionUsageLogsRepository>();
        services.AddScoped<IMeteredDimensionsRepository, MeteredDimensionsRepository>();
        services.AddScoped<IKnownUsersRepository, KnownUsersRepository>();
        services.AddScoped<IOffersRepository, OffersRepository>();
        services.AddScoped<IValueTypesRepository, ValueTypesRepository>();
        services.AddScoped<IOfferAttributesRepository, OfferAttributesRepository>();
        services.AddScoped<IEmailTemplateRepository, EmailTemplateRepository>();
        services.AddScoped<IPlanEventsMappingRepository, PlanEventsMappingRepository>();
        services.AddScoped<IEventsRepository, EventsRepository>();
        services.AddScoped<KnownUserAttribute>();
        services.AddScoped<IEmailService, SMTPEmailService>();
        services.AddScoped<ISAGitReleasesService, SAGitReleasesService>();
        services.AddScoped<ISchedulerFrequencyRepository, SchedulerFrequencyRepository>();
        services.AddScoped<IMeteredPlanSchedulerManagementRepository, MeteredPlanSchedulerManagementRepository>();
        services.AddScoped<ISchedulerManagerViewRepository, SchedulerManagerViewRepository>();
        services.AddScoped<SaaSClientLogger<HomeController>>();
        services.AddScoped<SaaSClientLogger<PlansController>>();
        services.AddScoped<SaaSClientLogger<OffersController>>();
        services.AddScoped<SaaSClientLogger<KnownUsersController>>();
        services.AddScoped<SaaSClientLogger<ApplicationLogController>>();
        services.AddScoped<SaaSClientLogger<ApplicationConfigController>>();
        services.AddScoped<SaaSClientLogger<SchedulerController>>();
    }
}