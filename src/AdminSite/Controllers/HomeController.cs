// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See LICENSE file in the project root for license information.
namespace Microsoft.Marketplace.Saas.Web.Controllers
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Text.Json;
    using System.Threading.Tasks;
    using Microsoft.AspNetCore.Authorization;
    using Microsoft.AspNetCore.Diagnostics;
    using Microsoft.AspNetCore.Http;
    using Microsoft.AspNetCore.Mvc;
    using Microsoft.AspNetCore.Mvc.Rendering;
    using Microsoft.Extensions.Logging;
    using Microsoft.Extensions.Options;
    using Microsoft.Marketplace.SaaS.SDK.Services.Configurations;
    using Microsoft.Marketplace.SaaS.SDK.Services.Contracts;
    using Microsoft.Marketplace.SaaS.SDK.Services.Exceptions;
    using Microsoft.Marketplace.SaaS.SDK.Services.Models;
    using Microsoft.Marketplace.SaaS.SDK.Services.Services;
    using Microsoft.Marketplace.SaaS.SDK.Services.StatusHandlers;
    using Microsoft.Marketplace.SaaS.SDK.Services.Utilities;
    using Microsoft.Marketplace.SaasKit.Client.DataAccess.Contracts;
    using Microsoft.Marketplace.SaasKit.Client.DataAccess.Entities;
    using Microsoft.Marketplace.SaasKit.Client.DataAccess.Services;

    /// <summary>
    /// Home Controller.
    /// </summary>
    /// <seealso cref="Microsoft.Marketplace.Saas.Web.Controllers.BaseController" />
    [ServiceFilter(typeof(KnownUserAttribute))]
    [ServiceFilter(typeof(LoggerActionFilter))]
    [ServiceFilter(typeof(ExceptionHandlerAttribute))]
    public class HomeController : BaseController
    {
        /// <summary>
        /// The logger.
        /// </summary>
        private readonly ILogger<HomeController> logger;

        private readonly IFulfillmentApiService fulfillApiService;
        private readonly IMeteredBillingApiService billingApiService;
        private readonly IEmailService emailService;

        private readonly SaaSApiClientConfiguration saaSApiClientConfiguration;

        private readonly ApplicationConfigService applicationConfigService;
        private readonly UserService userService;
        private readonly SubscriptionService subscriptionService;
        private readonly ApplicationLogService applicationLogService;
        private readonly OfferService offerService;
        private readonly PlanService planService;

        private readonly ISubscriptionLogRepository subscriptionLogRepository;
        private readonly ISubscriptionUsageLogsRepository subscriptionUsageLogRepository;
        private readonly IMeteredDimensionsRepository meteredDimensionsRepository;

        private readonly ISubscriptionStatusHandler pendingFulfillmentStatusHandlers;
        private readonly ISubscriptionStatusHandler pendingActivationStatusHandlers;
        private readonly ISubscriptionStatusHandler unsubscribeStatusHandlers;
        private readonly ISubscriptionStatusHandler notificationStatusHandlers;

        /// <summary>
        /// Initializes a new instance of the <see cref="HomeController" /> class.
        /// </summary>
        /// <param name="usersRepository">The users repository.</param>
        /// <param name="billingApiService">The billing API service.</param>
        /// <param name="logger">The logger.</param>
        /// <param name="subscriptionRepo">The subscription repo.</param>
        /// <param name="planRepository">The plan repository.</param>
        /// <param name="subscriptionUsageLogsRepository">The subscription usage logs repository.</param>
        /// <param name="dimensionsRepository">The dimensions repository.</param>
        /// <param name="subscriptionLogsRepo">The subscription logs repo.</param>
        /// <param name="applicationConfigRepository">The application configuration repository.</param>
        /// <param name="userRepository">The user repository.</param>
        /// <param name="fulfillApiService">The fulfill API client.</param>
        /// <param name="applicationLogRepository">The application log repository.</param>
        /// <param name="emailTemplateRepository">The email template repository.</param>
        /// <param name="planEventsMappingRepository">The plan events mapping repository.</param>
        /// <param name="eventsRepository">The events repository.</param>
        /// <param name="SaaSApiClientConfiguration">The SaaSApiClientConfiguration.</param>
        /// <param name="cloudConfigs">The cloud configs.</param>
        /// <param name="loggerFactory">The logger factory.</param>
        /// <param name="emailService">The email service.</param>
        /// <param name="offersRepository">The offers repository.</param>
        /// <param name="offersAttributeRepository">The offers attribute repository.</param>
        public HomeController(
                        SaaSApiClientConfiguration saaSApiClientConfiguration,

                        IMeteredBillingApiService billingApiService,
                        IFulfillmentApiService fulfillApiService,
                        IEmailService emailService,

                        ApplicationConfigService applicationConfigService,
                        ApplicationLogService applicationLogService,
                        UserService userService,
                        SubscriptionService subscriptionService,
                        OfferService offerService,
                        PlanService planService,

                        ISubscriptionLogRepository subscriptionLogRepository,
                        ISubscriptionUsageLogsRepository subscriptionUsageLogRepository,
                        IMeteredDimensionsRepository meteredDimensionsRepository,

                        PendingActivationStatusHandler pendingFulfillmentStatusHandlers,
                        PendingFulfillmentStatusHandler pendingActivationStatusHandlers,
                        NotificationStatusHandler unsubscribeStatusHandlers,
                        UnsubscribeStatusHandler notificationStatusHandlers,

                        ILogger<HomeController> logger
            )
        {
            this.saaSApiClientConfiguration = saaSApiClientConfiguration;

            this.billingApiService = billingApiService;
            this.fulfillApiService = fulfillApiService;
            this.emailService = emailService;

            this.applicationConfigService = applicationConfigService;
            this.userService = userService;
            this.applicationLogService = applicationLogService;
            this.subscriptionService = subscriptionService;
            this.offerService = offerService;
            this.planService = planService;

            this.subscriptionLogRepository = subscriptionLogRepository;
            this.subscriptionUsageLogRepository = subscriptionUsageLogRepository;
            this.meteredDimensionsRepository = meteredDimensionsRepository;

            this.pendingActivationStatusHandlers = pendingFulfillmentStatusHandlers;
            this.pendingFulfillmentStatusHandlers = pendingActivationStatusHandlers;
            this.notificationStatusHandlers = notificationStatusHandlers;
            this.unsubscribeStatusHandlers = unsubscribeStatusHandlers;

            this.logger = logger;
        }

        /// <summary>
        /// Indexes this instance.
        /// </summary>
        /// <returns> The <see cref="IActionResult" />.</returns>
        public IActionResult Index()
        {
            this.applicationConfigService.SaveFileToDisk("LogoFile", "contoso-sales.png");
            this.applicationConfigService.SaveFileToDisk("FaviconFile", "favicon.ico");

            var userId = this.userService.AddUser(this.GetCurrentUserDetail());

            if (this.saaSApiClientConfiguration.SupportMeteredBilling)
            {
                this.TempData.Add("SupportMeteredBilling", "1");
                this.HttpContext.Session.SetString("SupportMeteredBilling", "1");
            }
            return this.View();
        }

        /// <summary>
        /// Gets or sets the <see cref="Microsoft.AspNetCore.Mvc.IActionResult" /> with the specified error.
        /// </summary>
        /// <returns>
        /// The <see cref="IActionResult" />.
        /// </returns>
        /// <value>
        /// The <see cref="Microsoft.AspNetCore.Mvc.IActionResult" />.
        /// </value>
        public IActionResult ActivatedMessage()
        {
            return this.View("ProcessMessage");
        }

        /// <summary>
        /// The Error.
        /// </summary>
        /// <returns>
        /// The <see cref="IActionResult" />.
        /// </returns>
        [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
        public IActionResult Error()
        {
            var exceptionDetail = this.HttpContext.Features.Get<IExceptionHandlerFeature>();
            return this.View(exceptionDetail?.Error);
        }


        /// <summary>
        /// Subscriptionses this instance.
        /// </summary>
        /// <returns> The <see cref="IActionResult" />.</returns>
        public IActionResult Subscriptions()
        {
            var subscriptionDetail = new SubscriptionViewModel();
            this.TempData["ShowWelcomeScreen"] = "True";

            subscriptionDetail.Subscriptions = this.subscriptionService.GetAll(true);

            if (this.TempData["ErrorMsg"] != null)
            {
                subscriptionDetail.IsSuccess = false;
                subscriptionDetail.ErrorMessage = Convert.ToString(this.TempData["ErrorMsg"]);
            }
            return this.View(subscriptionDetail);
        }



        /// <summary>
        /// Subscriptions the details.
        /// </summary>
        /// <param name="subscriptionId">The subscription identifier.</param>
        /// <param name="planId">The plan identifier.</param>
        /// <returns> The <see cref="IActionResult" />.</returns>
        public async Task<IActionResult> SubscriptionDetails(Guid subscriptionId, string planId)
        {
            this.TempData["ShowWelcomeScreen"] = false;

            var detailsFromAPI = await this.fulfillApiService.GetSubscriptionByIdAsync(subscriptionId).ConfigureAwait(false);
            var subscriptionDetail = new SubscriptionResultExtension();
            subscriptionDetail = this.subscriptionService.GetBySubscriptionId(subscriptionId);
            subscriptionDetail.SubscriptionParameters = this.subscriptionService.GetSubscriptionsParametersById(subscriptionId, subscriptionDetail.GuidPlanId);
            subscriptionDetail.Beneficiary = detailsFromAPI.Beneficiary;
            subscriptionDetail.ShowWelcomeScreen = false;

            this.logger.LogInformation("SubscriptonDetail :{0}", JsonSerializer.Serialize(subscriptionDetail));

            return this.View(subscriptionDetail);
        }

        /// <summary>
        /// Subscriptions the operation.
        /// </summary>
        /// <param name="subscriptionId">The subscription identifier.</param>
        /// <param name="planId">The plan identifier.</param>
        /// <param name="operation">The operation.</param>
        /// <param name="numberofProviders">The numberof providers.</param>
        /// <returns> The <see cref="IActionResult" />.</returns>
        public IActionResult SubscriptionDetailsOperation(Guid subscriptionId, string planId, string operation, int numberofProviders)
        {
            var userDetails = this.userService.GetUserFromEmailAddress(this.CurrentUserEmailAddress);
            var oldValue = this.subscriptionService.GetBySubscriptionId(subscriptionId);
            if (operation == "Activate")
            {
                if (oldValue.SubscriptionStatus.ToString() != SubscriptionStatusEnumExtension.PendingActivation.ToString())
                {
                    this.subscriptionService.UpdateSubscriptionStatus(subscriptionId, SubscriptionStatusEnumExtension.PendingActivation.ToString(), true);
                    SubscriptionAuditLogs auditLog = new SubscriptionAuditLogs()
                    {
                        Attribute = Convert.ToString(SubscriptionLogAttributes.Status),
                        SubscriptionId = oldValue.SubscribeId,
                        NewValue = SubscriptionStatusEnumExtension.PendingActivation.ToString(),
                        OldValue = oldValue.SubscriptionStatus.ToString(),
                        CreateBy = userDetails.UserId,
                        CreateDate = DateTime.Now,
                    };
                    this.subscriptionLogRepository.Save(auditLog);
                }

                this.pendingActivationStatusHandlers.Process(subscriptionId);
            }

            if (operation == "Deactivate")
            {
                this.subscriptionService.UpdateSubscriptionStatus(subscriptionId, SubscriptionStatusEnumExtension.PendingUnsubscribe.ToString(), true);
                SubscriptionAuditLogs auditLog = new SubscriptionAuditLogs()
                {
                    Attribute = Convert.ToString(SubscriptionLogAttributes.Status),
                    SubscriptionId = oldValue.SubscribeId,
                    NewValue = SubscriptionStatusEnumExtension.PendingUnsubscribe.ToString(),
                    OldValue = oldValue.SubscriptionStatus.ToString(),
                    CreateBy = userDetails.UserId,
                    CreateDate = DateTime.Now,
                };
                this.subscriptionLogRepository.Save(auditLog);

                this.unsubscribeStatusHandlers.Process(subscriptionId);
            }

            this.notificationStatusHandlers.Process(subscriptionId);

            return this.RedirectToAction(nameof(this.ActivatedMessage));
        }


        /// <summary>
        /// Subscriptions the log detail.
        /// </summary>
        /// <param name="subscriptionId">The subscription identifier.</param>
        /// <returns>
        /// Subscription log detail.
        /// </returns>
        public IActionResult ViewSubscriptionLogDetail(Guid subscriptionId)
        {
            var subscriptionAudit = this.subscriptionLogRepository.GetSubscriptionBySubscriptionId(subscriptionId).ToList();
            return this.View(subscriptionAudit);
        }

        /// <summary>
        /// Get Subscription Details for selected Subscription.
        /// </summary>
        /// <param name="subscriptionId">The subscription identifier.</param>
        /// <returns>
        /// The <see cref="IActionResult" />.
        /// </returns>
        public IActionResult ViewChangeSubscriptionDetail(Guid subscriptionId, string viewName)
        {
            var subscriptionDetail = this.subscriptionService.GetBySubscriptionId(subscriptionId);
            subscriptionDetail.PlanList = this.subscriptionService.GetAllSubscriptionPlans();
            return this.View(viewName, subscriptionDetail);
        }

        /// <summary>
        /// Changes the quantity plan.
        /// </summary>
        /// <param name="subscriptionDetail">The subscription detail.</param>
        /// <returns>Changes subscription quantity.</returns>
        [HttpPost]
        public async Task<IActionResult> ChangeSubscriptionQuantity(SubscriptionResult subscriptionDetail)
        {
            if (subscriptionDetail != null && subscriptionDetail.Id != default && subscriptionDetail.Quantity > 0)
            {
                try
                {
                    //initiate change quantity
                    var currentUserId = this.userService.GetUserFromEmailAddress(this.CurrentUserEmailAddress);
                    var jsonResult = await this.fulfillApiService.ChangeQuantityForSubscriptionAsync(subscriptionDetail.Id, subscriptionDetail.Quantity).ConfigureAwait(false);
                    var changeQuantityOperationStatus = OperationStatusEnum.InProgress;

                    if (jsonResult != null && jsonResult.OperationId != default)
                    {
                        int _counter = 0;

                        //loop untill the operation status has moved away from inprogress or notstarted, generally this will be the result of webhooks' action aganist this operation
                        while (OperationStatusEnum.InProgress.Equals(changeQuantityOperationStatus) || OperationStatusEnum.NotStarted.Equals(changeQuantityOperationStatus))
                        {
                            var changeQuantityOperationResult = await this.fulfillApiService.GetOperationStatusResultAsync(subscriptionDetail.Id, jsonResult.OperationId).ConfigureAwait(false);
                            changeQuantityOperationStatus = changeQuantityOperationResult.Status;

                            this.logger.LogInformation($"Quantity Change Progress. SubscriptionId: {subscriptionDetail.Id} ToQuantity: {subscriptionDetail.Quantity} UserId: {currentUserId} OperationId: {jsonResult.OperationId} Operationstatus: {changeQuantityOperationStatus}.");
                            await this.applicationLogService.AddApplicationLog($"Quantity Change Progress. SubscriptionId: {subscriptionDetail.Id} ToQuantity: {subscriptionDetail.Quantity} UserId: {currentUserId} OperationId: {jsonResult.OperationId} Operationstatus: {changeQuantityOperationStatus}.").ConfigureAwait(false);

                            //wait and check every 5secs
                            await Task.Delay(5000);
                            _counter++;
                            if (_counter > 100)
                            {
                                //if loop has been executed for more than 100 times then break, to avoid infinite loop just in case
                                break;
                            }
                        }

                        if (changeQuantityOperationStatus == OperationStatusEnum.Succeeded)
                        {
                            this.logger.LogInformation($"Quantity Change Success. SubscriptionId: {subscriptionDetail.Id} ToQuantity: {subscriptionDetail.Quantity} UserId: {currentUserId} OperationId: {jsonResult.OperationId}.");
                            await this.applicationLogService.AddApplicationLog($"Quantity Change Success. SubscriptionId: {subscriptionDetail.Id} ToQuantity: {subscriptionDetail.Quantity} UserId: {currentUserId} OperationId: {jsonResult.OperationId}.").ConfigureAwait(false);
                        }
                        else
                        {
                            this.logger.LogInformation($"Quantity Change Failed. SubscriptionId: {subscriptionDetail.Id} ToQuantity: {subscriptionDetail.Quantity} UserId: {currentUserId} OperationId: {jsonResult.OperationId} Operationstatus: {changeQuantityOperationStatus}.");
                            await this.applicationLogService.AddApplicationLog($"Quantity Change Failed. SubscriptionId: {subscriptionDetail.Id} ToQuantity: {subscriptionDetail.Quantity} UserId: {currentUserId} OperationId: {jsonResult.OperationId} Operationstatus: {changeQuantityOperationStatus}.").ConfigureAwait(false);

                            throw new MarketplaceException($"Quantity Change operation failed with operation status {changeQuantityOperationStatus}. Check if the updates are allowed in the App config \"AcceptSubscriptionUpdates\" key or db application log for more information.");
                        }
                    }
                }
                catch (MarketplaceException fex)
                {
                    this.TempData["ErrorMsg"] = fex.Message;
                    this.logger.LogError("Message:{0} :: {1}   ", fex.Message, fex.InnerException);
                }
            }

            return this.RedirectToAction(nameof(this.Subscriptions));
        }

        /// <summary>
        /// Changes the subscription plan.
        /// </summary>
        /// <param name="subscriptionDetail">The subscription detail.</param>
        /// <returns> IActionResult.</returns>
        [HttpPost]
        public async Task<IActionResult> ChangeSubscriptionPlan(SubscriptionResult subscriptionDetail)
        {
            if (subscriptionDetail.Id != default && !string.IsNullOrEmpty(subscriptionDetail.PlanId))
            {
                try
                {
                    //initiate change plan
                    var currentUserId = this.userService.GetUserFromEmailAddress(this.CurrentUserEmailAddress);
                    var jsonResult = await this.fulfillApiService.ChangePlanForSubscriptionAsync(subscriptionDetail.Id, subscriptionDetail.PlanId).ConfigureAwait(false);
                    var changePlanOperationStatus = OperationStatusEnum.InProgress;

                    if (jsonResult != null && jsonResult.OperationId != default)
                    {
                        int _counter = 0;

                        //loop untill the operation status has moved away from inprogress or notstarted, generally this will be the result of webhooks' action aganist this operation
                        while (OperationStatusEnum.InProgress.Equals(changePlanOperationStatus) || OperationStatusEnum.NotStarted.Equals(changePlanOperationStatus))
                        {
                            var changePlanOperationResult = await this.fulfillApiService.GetOperationStatusResultAsync(subscriptionDetail.Id, jsonResult.OperationId).ConfigureAwait(false);
                            changePlanOperationStatus = changePlanOperationResult.Status;

                            this.logger.LogInformation($"Plan Change Progress. SubscriptionId: {subscriptionDetail.Id} ToPlan: {subscriptionDetail.PlanId} UserId: {currentUserId.UserId} OperationId: {jsonResult.OperationId} Operationstatus: {changePlanOperationStatus}.");
                            await this.applicationLogService.AddApplicationLog($"Plan Change Progress. SubscriptionId: {subscriptionDetail.Id} ToPlan: {subscriptionDetail.PlanId} UserId: {currentUserId.UserId} OperationId: {jsonResult.OperationId} Operationstatus: {changePlanOperationStatus}.").ConfigureAwait(false);

                            //wait and check every 5secs
                            await Task.Delay(5000);
                            _counter++;
                            if (_counter > 100)
                            {
                                //if loop has been executed for more than 100 times then break, to avoid infinite loop just in case
                                break;
                            }
                        }

                        if (changePlanOperationStatus == OperationStatusEnum.Succeeded)
                        {
                            this.logger.LogInformation($"Plan Change Success. SubscriptionId: {subscriptionDetail.Id} ToPlan : {subscriptionDetail.PlanId} UserId: {currentUserId.UserId} OperationId: {jsonResult.OperationId}.");
                            await this.applicationLogService.AddApplicationLog($"Plan Change Success. SubscriptionId: {subscriptionDetail.Id} ToPlan: {subscriptionDetail.PlanId} UserId: {currentUserId.UserId} OperationId: {jsonResult.OperationId}.").ConfigureAwait(false);
                        }
                        else
                        {
                            this.logger.LogInformation($"Plan Change Failed. SubscriptionId: {subscriptionDetail.Id} ToPlan : {subscriptionDetail.PlanId} UserId: {currentUserId.UserId} OperationId: {jsonResult.OperationId} Operation status {changePlanOperationStatus}.");
                            await this.applicationLogService.AddApplicationLog($"Plan Change Failed. SubscriptionId: {subscriptionDetail.Id} ToPlan: {subscriptionDetail.PlanId} UserId: {currentUserId.UserId} OperationId: {jsonResult.OperationId} Operation status {changePlanOperationStatus}.").ConfigureAwait(false);

                            throw new MarketplaceException($"Plan change operation failed with operation status {changePlanOperationStatus}. Check if the updates are allowed in the App config \"AcceptSubscriptionUpdates\" key or db application log for more information.");
                        }
                    }
                }
                catch (MarketplaceException fex)
                {
                    this.TempData["ErrorMsg"] = fex.Message;
                }
            }

            return this.RedirectToAction(nameof(this.Subscriptions));
        }


        /// <summary>
        /// Records the usage.
        /// </summary>
        /// <param name="subscriptionId">The subscription identifier.</param>
        /// <returns> The <see cref="IActionResult" />.</returns>
        public IActionResult RecordUsage(Guid subscriptionId)
        {
            var subscriptionDetail = this.subscriptionService.GetBySubscriptionId(subscriptionId);
            var allDimensionsList = this.meteredDimensionsRepository.GetDimensionsByPlanId(subscriptionDetail.PlanId);
            var usageViewModel = new SubscriptionUsageViewModel
            {
                SubscriptionDetail = subscriptionDetail,
                MeteredAuditLogs = this.subscriptionUsageLogRepository.GetMeteredAuditLogsBySubscriptionId(subscriptionDetail.SubscribeId).OrderByDescending(s => s.CreatedDate).ToList(),
                DimensionsList = new SelectList(allDimensionsList, "Dimension", "Description"),
            };
            return this.View(usageViewModel);
        }

        /// <summary>
        /// Manages the subscription usage.
        /// </summary>
        /// <param name="subscriptionData">The subscription data.</param>
        /// <returns> The <see cref="IActionResult" />.</returns>
        [HttpPost]
        public IActionResult RecordSubscriptionUsage(SubscriptionUsageViewModel subscriptionData)
        {
            if (subscriptionData != null && subscriptionData.SubscriptionDetail != null)
            {
                var currentUserDetail = this.userService.GetUserFromEmailAddress(this.CurrentUserEmailAddress);
                var subscriptionUsageRequest = new MeteringUsageRequest()
                {
                    Dimension = subscriptionData.SelectedDimension,
                    EffectiveStartTime = DateTime.UtcNow,
                    PlanId = subscriptionData.SubscriptionDetail.PlanId,
                    Quantity = Convert.ToDouble(subscriptionData.Quantity ?? "0"),
                    ResourceId = subscriptionData.SubscriptionDetail.Id,
                };
                var meteringUsageResult = new MeteringUsageResult();
                var requestJson = JsonSerializer.Serialize(subscriptionUsageRequest);
                var responseJson = string.Empty;
                try
                {
                    this.logger.LogInformation("EmitUsageEventAsync");
                    meteringUsageResult = this.billingApiService.EmitUsageEventAsync(subscriptionUsageRequest).ConfigureAwait(false).GetAwaiter().GetResult();
                    responseJson = JsonSerializer.Serialize(meteringUsageResult);
                    this.logger.LogInformation(responseJson);
                }
                catch (MarketplaceException mex)
                {
                    responseJson = JsonSerializer.Serialize(mex.MeteredBillingErrorDetail);
                    meteringUsageResult.Status = mex.ErrorCode;
                    this.logger.LogInformation(responseJson);
                }

                var newMeteredAuditLog = new MeteredAuditLogs()
                {
                    RequestJson = requestJson,
                    ResponseJson = responseJson,
                    StatusCode = meteringUsageResult.Status,
                    SubscriptionId = subscriptionData.SubscriptionDetail.SubscribeId,
                    SubscriptionUsageDate = DateTime.UtcNow,
                    CreatedBy = currentUserDetail == null ? 0 : currentUserDetail.UserId,
                    CreatedDate = DateTime.Now,
                };
                this.subscriptionUsageLogRepository.Save(newMeteredAuditLog);
            }
            return this.RedirectToAction(nameof(this.RecordUsage), new { subscriptionId = subscriptionData.SubscriptionDetail.Id });
        }


        [HttpPost]
        public IActionResult FetchAllSubscriptions()
        {
            try
            {
                var currentUser = this.userService.GetUserFromEmailAddress(this.CurrentUserEmailAddress);
                // Step 1: Get all subscriptions from the API
                var subscriptions = this.fulfillApiService.GetAllSubscriptionAsync().GetAwaiter().GetResult();
                foreach (SubscriptionResult subscription in subscriptions)
                {
                    // Step 2: Check if they Exist in DB - Create if dont exist
                    if (this.subscriptionService.GetBySubscriptionId(subscription.Id)?.Name == null)
                    {
                        // Step 3: Add/Update the Offer
                        Guid OfferId = this.offerService.AddOffer(subscription.OfferId, currentUser.UserId);

                        // Step 4: Add/Update the Plans. For Unsubscribed Only Add current plan from subscription information
                        if (subscription.SaasSubscriptionStatus == SubscriptionStatusEnum.Unsubscribed)
                        {
                            this.planService.Add(new PlanDetailResultExtension
                            {
                                PlanId = subscription.PlanId,
                                DisplayName = subscription.PlanId,
                                Description = "",
                                OfferId = OfferId,
                                PlanGUID = Guid.NewGuid(),
                                IsPerUserPlan = subscription.Quantity > 0,
                            });
                        }
                        else
                        {
                            var subscriptionPlans = this.fulfillApiService.GetAllPlansForSubscriptionAsync(subscription.Id, OfferId).ConfigureAwait(false).GetAwaiter().GetResult();
                            this.planService.Add(subscriptionPlans.ToArray());
                        }

                        // Step 5: Add/Update the current user from Subscription information
                        var customerUserId = this.userService.AddUser(new UserModel { FullName = subscription.Beneficiary.EmailId, EmailAddress = subscription.Beneficiary.EmailId });
                    }
                    // Step 6: Add Subscription
                    var subscribeId = this.subscriptionService.AddSubscription(subscription, currentUser, currentUser.UserId);

                    // Step 7: Add Subscription Audit
                    if (subscribeId > 0 && subscription.SaasSubscriptionStatus == SubscriptionStatusEnum.PendingFulfillmentStart)
                    {
                        SubscriptionAuditLogs auditLog = new SubscriptionAuditLogs()
                        {
                            Attribute = Convert.ToString(SubscriptionLogAttributes.Status),
                            SubscriptionId = subscribeId,
                            NewValue = SubscriptionStatusEnum.PendingFulfillmentStart.ToString(),
                            OldValue = "None",
                            CreateBy = currentUser.UserId,
                            CreateDate = DateTime.Now,
                        };
                        this.subscriptionLogRepository.Save(auditLog);
                    }
                }

                return Ok();
            }
            catch (Exception ex)
            {
                var errorMessage = $"Message: {ex.Message} ({ex.InnerException})";
                logger.LogError(errorMessage);
                applicationLogService.AddApplicationLog(errorMessage).GetAwaiter().GetResult();

                return BadRequest();
            }
        }
    }
}