// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See LICENSE file in the project root for license information.

using Microsoft.Marketplace.SaaS.SDK.Services.Contracts;

namespace Microsoft.Marketplace.SaasKit.Client.Controllers
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Text.Json;
    using System.Threading.Tasks;
    using Microsoft.AspNetCore.Authentication;
    using Microsoft.AspNetCore.Authentication.OpenIdConnect;
    using Microsoft.AspNetCore.Diagnostics;
    using Microsoft.AspNetCore.Mvc;
    using Microsoft.Extensions.Logging;
    using Microsoft.Marketplace.SaaS.SDK.Services.Configurations;
    using Microsoft.Marketplace.SaaS.SDK.Services.Exceptions;
    using Microsoft.Marketplace.SaaS.SDK.Services.Models;
    using Microsoft.Marketplace.SaaS.SDK.Services.Services;
    using Microsoft.Marketplace.SaaS.SDK.Services.StatusHandlers;
    using Microsoft.Marketplace.SaaS.SDK.Services.Utilities;
    using Microsoft.Marketplace.SaasKit.Client.DataAccess.Contracts;
    using Microsoft.Marketplace.SaasKit.Client.DataAccess.Entities;
    using Microsoft.Marketplace.SaasKit.Client.DataAccess.Services;

    /// <summary>Home Controller.</summary>
    /// <seealso cref="BaseController"/>
    [ServiceFilter(typeof(LoggerActionFilter))]
    [ServiceFilter(typeof(ExceptionHandlerAttribute))]
    public class HomeController : BaseController
    {
        /// <summary>
        /// The logger.
        /// </summary>
        private readonly ILogger<HomeController> logger;

        private readonly IFulfillmentApiService fulfillApiService;
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
                        CurrentUserComponent currentUserComponent,

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
            ):base(currentUserComponent)
        {
            this.saaSApiClientConfiguration = saaSApiClientConfiguration;

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
        /// Get All Subscription List for Current Logged in User.
        /// </summary>
        /// <param name="token">The MS Token<see cref="string" />..</param>
        /// <returns>
        /// The <see cref="IActionResult" />.
        /// </returns>
        public async Task<IActionResult> Index(string token = null)
        {
            this.applicationConfigService.SaveFileToDisk("LogoFile", "contoso-sales.png");
            this.applicationConfigService.SaveFileToDisk("FaviconFile", "favicon.ico");

            //No token provided -> show welcome screen regardless of user login state
            if (string.IsNullOrEmpty(token))
            {
                this.logger.LogInformation($"Landing page - no token provided");

                this.TempData["ShowWelcomeScreen"] = "True";
                return this.View(new SubscriptionResultExtension() { ShowWelcomeScreen = true });
            }

            //Token provided - if user not authenticated redirect to login 
            if (!this.User.Identity.IsAuthenticated)
            {
                this.logger.LogInformation($"Landing page - with token, user not auth");
                return this.Challenge(
                    new AuthenticationProperties { RedirectUri = "/?token=" + token },
                    OpenIdConnectDefaults.AuthenticationScheme);
            }

            //Token Provided, user authenticated
            this.logger.LogInformation($"Landing page - with token, user authenticated");
            this.TempData["ShowWelcomeScreen"] = null;
            token = token.Replace(' ', '+');

            var newSubscription = await this.fulfillApiService.ResolveAsync(token).ConfigureAwait(false);
            if (newSubscription?.SubscriptionId == default)
            {
                //the ResolveAsync always throws exception if failure so we should not ever get a null result.
                throw new Exception("FulfillmentAPI.ResolveAsync failed. Check error logs for more details.");
            }

            var offerId = this.offerService.AddOffer(newSubscription.OfferId, _currentUserComponent.UserId);

            var subscriptionPlanDetail = await this.fulfillApiService.GetAllPlansForSubscriptionAsync(newSubscription.SubscriptionId, offerId).ConfigureAwait(false);
            this.planService.Add(subscriptionPlanDetail.ToArray());

            var currentPlan = this.planService.GetPlanById(newSubscription.PlanId, offerId);
            var subscriptionData = await this.fulfillApiService.GetSubscriptionByIdAsync(newSubscription.SubscriptionId).ConfigureAwait(false);
            var subscriptionId = this.subscriptionService.AddSubscription(subscriptionData, _currentUserComponent.UserId);

            var subscriptionExtension = this.subscriptionService.GetBySubscriptionId(newSubscription.SubscriptionId, true);
            subscriptionExtension.SubscriptionParameters = this.subscriptionService.GetSubscriptionsParametersById(newSubscription.SubscriptionId, subscriptionExtension.GuidPlanId);
            subscriptionExtension.ShowWelcomeScreen = false;
            subscriptionExtension.CustomerEmailAddress = _currentUserComponent.Email;
            subscriptionExtension.CustomerName = _currentUserComponent.Name;
            subscriptionExtension.IsAutomaticProvisioningSupported = Convert.ToBoolean(this.applicationConfigService.GetValueByName("IsAutomaticProvisioningSupported"));

            return this.View(subscriptionExtension);
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
        /// Processes the message.
        /// </summary>
        /// <param name="action">The action.</param>
        /// <param name="status">The status.</param>
        /// <returns>
        /// Return View.
        /// </returns>
        public IActionResult ProcessMessage(string action, string status)
        {
            if (status.Equals("Activate"))
            {
                return this.PartialView();
            }
            else
            {
                return this.View();
            }
        }


        /// <summary>
        /// Subscription this instance.
        /// </summary>
        /// <returns> Subscription instance.</returns>
        public IActionResult Subscriptions()
        {
            if (!this.User.Identity.IsAuthenticated)
            {
                return this.RedirectToAction(nameof(this.Index));
            }

            this.TempData["ShowWelcomeScreen"] = "True";
            var subscriptionDetail = new SubscriptionViewModel();
            subscriptionDetail.Subscriptions = this.subscriptionService.GetByCustomerEmail(_currentUserComponent.Email, true).ToList();
            foreach (var subscription in subscriptionDetail.Subscriptions)
            {
                subscription.IsAutomaticProvisioningSupported = Convert.ToBoolean(this.applicationConfigService.GetValueByName("IsAutomaticProvisioningSupported"));
            }

            subscriptionDetail.SaaSAppUrl = this.fulfillApiService.GetSaaSAppURL();

            if (this.TempData["ErrorMsg"] != null)
            {
                subscriptionDetail.IsSuccess = false;
                subscriptionDetail.ErrorMessage = Convert.ToString(this.TempData["ErrorMsg"]);
            }

            return this.View(subscriptionDetail);
        }


        /// <summary>
        /// Get All Subscription List for Current Logged in User.
        /// </summary>
        /// <param name="subscriptionId">The subscription identifier.</param>
        /// <returns>
        /// The <see cref="IActionResult" />.
        /// </returns>
        public IActionResult SubscriptionDetail(Guid subscriptionId)
        {
            if (!this.User.Identity.IsAuthenticated)
            {
                return this.RedirectToAction(nameof(this.Index));
            }

            var subscriptionDetail = this.subscriptionService.GetByCustomerEmail(_currentUserComponent.Email).FirstOrDefault(s => s.Id == subscriptionId);
            if (subscriptionDetail == null)
            {
                logger.LogError($"Subscription with id {subscriptionId} not found.");
                return this.RedirectToAction(nameof(this.Index));
            }
            subscriptionDetail.PlanList = this.subscriptionService.GetAllSubscriptionPlans();

            return this.View(subscriptionDetail);
        }

        /// <summary>
        /// Get Subscription Details for selected Subscription.
        /// </summary>
        /// <param name="subscriptionId">The subscription identifier.</param>
        /// <returns>
        /// The <see cref="IActionResult" />.
        /// </returns>
        public IActionResult SubscriptionQuantityDetail(Guid subscriptionId)
        {
            if (!this.User.Identity.IsAuthenticated)
            {
                return this.RedirectToAction(nameof(this.Index));
            }

            var subscriptionDetail = this.subscriptionService.GetByCustomerEmail(_currentUserComponent.Email).FirstOrDefault(s => s.Id == subscriptionId);
            return this.View(subscriptionDetail);
        }

        /// <summary>
        /// Subscriptions the log detail.
        /// </summary>
        /// <param name="subscriptionId">The subscription identifier.</param>
        /// <returns> Subscription log detail.</returns>
        public IActionResult SubscriptionLogDetail(Guid subscriptionId)
        {
            if (!this.User.Identity.IsAuthenticated)
            {
                return this.RedirectToAction(nameof(this.Index));
            }

            var subscriptionAudit = this.subscriptionLogRepository.GetSubscriptionBySubscriptionId(subscriptionId).ToList();
            return this.View(subscriptionAudit);
        }


        /// <summary>
        /// Subscriptions the details.
        /// </summary>
        /// <param name="subscriptionId">The subscription identifier.</param>
        /// <param name="planId">The plan identifier.</param>
        /// <param name="operation">The operation.</param>
        /// <returns> Subscription Detials.</returns>
        public IActionResult SubscriptionDetails(Guid subscriptionId, string planId, string operation)
        {
            if (!this.User.Identity.IsAuthenticated)
            {
                return this.RedirectToAction(nameof(this.Index));
            }

            this.TempData["ShowWelcomeScreen"] = false;
            var subscriptionDetail = this.subscriptionService.GetBySubscriptionId(subscriptionId);
            var subscriptionParmeters = this.subscriptionService.GetSubscriptionsParametersById(subscriptionId, subscriptionDetail.GuidPlanId);
            subscriptionDetail.SubscriptionParameters = subscriptionParmeters.Where(s => s.Type.ToLower() == "input").ToList();
            subscriptionDetail.CustomerEmailAddress = _currentUserComponent.Email;
            subscriptionDetail.CustomerName = _currentUserComponent.Name;
            subscriptionDetail.IsAutomaticProvisioningSupported = Convert.ToBoolean(this.applicationConfigService.GetValueByName("IsAutomaticProvisioningSupported"));

            return this.View("Index", subscriptionDetail);
        }

        /// <summary>
        /// Subscriptions the operation.
        /// </summary>
        /// <param name="subscriptionResultExtension">The subscription result extension.</param>
        /// <param name="subscriptionId">The subscription identifier.</param>
        /// <param name="planId">The plan identifier.</param>
        /// <param name="operation">The operation.</param>
        /// <returns>
        /// Subscriptions operation.
        /// </returns>
        [HttpPost]
        public IActionResult SubscriptionOperation(SubscriptionResultExtension subscriptionResultExtension, Guid subscriptionId, string planId, string operation)
        {
            if (!this.User.Identity.IsAuthenticated)
            {
                return this.RedirectToAction(nameof(this.Index));
            }

            if (subscriptionId == default)
            {
                logger.LogWarning("SubscriptionOperation called with no subscriptionId. Exiting.");
                return this.RedirectToAction(nameof(this.Index));
            }

            var oldValue = this.subscriptionService.GetByCustomerEmail(_currentUserComponent.Email, true).FirstOrDefault();
            var currentUser = this.userService.GetUserFromEmailAddress(_currentUserComponent.Email);

            if (operation == "Activate")
            {
                try
                {
                    this.logger.LogInformation("Save Subscription Parameters:  {0}", JsonSerializer.Serialize(subscriptionResultExtension.SubscriptionParameters));
                    var inputParams = subscriptionResultExtension.SubscriptionParameters?.Where(s => s.Type.ToLower() == "input") ?? new List<SubscriptionParametersModel>();
                    this.subscriptionService.AddSubscriptionParameters(currentUser, inputParams.ToArray());

                    if (Convert.ToBoolean(this.applicationConfigService.GetValueByName("IsAutomaticProvisioningSupported")))
                    {
                        this.logger.LogInformation("UpdateStateOfSubscription PendingActivation: SubscriptionId: {0} ", subscriptionId);
                        this.subscriptionService.UpdateSubscriptionStatus(subscriptionId, SubscriptionStatusEnumExtension.PendingActivation.ToString(), true);
                        this.pendingActivationStatusHandlers.Process(subscriptionId);
                    }
                    else
                    {
                        this.pendingFulfillmentStatusHandlers.Process(subscriptionId);
                    }
                }
                catch (MarketplaceException fex)
                {
                    this.logger.LogInformation(fex.Message);
                }
            }

            if (operation == "Deactivate")
            {
                this.subscriptionService.UpdateSubscriptionStatus(subscriptionId, SubscriptionStatusEnumExtension.PendingUnsubscribe.ToString(), true);
                this.unsubscribeStatusHandlers.Process(subscriptionId);
            }


            this.notificationStatusHandlers.Process(subscriptionId);

            return this.RedirectToAction(nameof(this.ProcessMessage), new { action = operation, status = operation });
        }

        /// <summary>
        /// Changes the subscription plan.
        /// </summary>
        /// <param name="subscriptionDetail">The subscription detail.</param>
        /// <returns>Changes subscription plan.</returns>
        [HttpPost]
        public async Task<IActionResult> ChangeSubscriptionPlan(SubscriptionResult subscriptionDetail)
        {
            if (!this.User.Identity.IsAuthenticated)
            {
                return this.RedirectToAction(nameof(this.Index));
            }
            if (subscriptionDetail.Id == default || string.IsNullOrEmpty(subscriptionDetail.PlanId))
            {
                logger.LogWarning("ChangeSubscriptionPlan called with no subscriptionId or no Plan. Exiting.");
                return this.RedirectToAction(nameof(this.Subscriptions));
            }

            try
            {
                //initiate change plan
                var currentUser = this.userService.GetUserFromEmailAddress(_currentUserComponent.Email);
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

                        var logMsg = $"Plan Change Progress. SubscriptionId: {subscriptionDetail.Id} ToPlan: {subscriptionDetail.PlanId} UserId: {currentUser.UserId} OperationId: {jsonResult.OperationId} Operationstatus: {changePlanOperationStatus}.";
                        this.logger.LogInformation(logMsg);
                        await this.applicationLogService.AddApplicationLog(logMsg).ConfigureAwait(false);

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
                        var logMsg = $"Plan Change Success. SubscriptionId: {subscriptionDetail.Id} ToPlan : {subscriptionDetail.PlanId} UserId: {currentUser.UserId} OperationId: {jsonResult.OperationId}.";
                        this.logger.LogInformation(logMsg);
                        await this.applicationLogService.AddApplicationLog(logMsg).ConfigureAwait(false);
                    }
                    else
                    {
                        var logMsg = $"Plan Change Failed. SubscriptionId: {subscriptionDetail.Id} ToPlan : {subscriptionDetail.PlanId} UserId: {currentUser.UserId} OperationId: {jsonResult.OperationId} Operation status {changePlanOperationStatus}.";
                        this.logger.LogInformation(logMsg);
                        await this.applicationLogService.AddApplicationLog(logMsg).ConfigureAwait(false);

                        throw new MarketplaceException($"Plan change operation failed with operation status {changePlanOperationStatus}.");
                    }
                }
            }
            catch (MarketplaceException fex)
            {
                this.TempData["ErrorMsg"] = fex.Message;
            }

            return this.RedirectToAction(nameof(this.Subscriptions));
        }

        /// <summary>
        /// Changes the quantity plan.
        /// </summary>
        /// <param name="subscriptionDetail">The subscription detail.</param>
        /// <returns>Changes subscription quantity.</returns>
        [HttpPost]
        public async Task<IActionResult> ChangeSubscriptionQuantity(SubscriptionResult subscriptionDetail)
        {
            if (!this.User.Identity.IsAuthenticated)
            {
                return this.RedirectToAction(nameof(this.Index));
            }
            if (subscriptionDetail.Id == default || string.IsNullOrEmpty(subscriptionDetail.PlanId))
            {
                logger.LogWarning("ChangeSubscriptionPlan called with no subscriptionId or no Plan. Exiting.");
                return this.RedirectToAction(nameof(this.Subscriptions));
            }

            try
            {
                //initiate change quantity
                var currentUserId = this.userService.GetUserFromEmailAddress(_currentUserComponent.Email);
                var jsonResult = await this.fulfillApiService.ChangeQuantityForSubscriptionAsync(subscriptionDetail.Id, subscriptionDetail.Quantity).ConfigureAwait(false);
                var changeQuantityOperationStatus = OperationStatusEnum.InProgress;
                if (jsonResult != null && jsonResult.OperationId != default)
                {
                    int _counter = 0;

                    while (OperationStatusEnum.InProgress.Equals(changeQuantityOperationStatus) || OperationStatusEnum.NotStarted.Equals(changeQuantityOperationStatus))
                    {
                        //loop untill the operation status has moved away from inprogress or notstarted, generally this will be the result of webhooks' action aganist this operation
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

                        throw new MarketplaceException($"Quantity Change operation failed with operation status {changeQuantityOperationStatus}.");
                    }
                }
            }
            catch (MarketplaceException fex)
            {
                this.TempData["ErrorMsg"] = fex.Message;
                this.logger.LogError("Message:{0} :: {1}   ", fex.Message, fex.InnerException);
            }
            return this.RedirectToAction(nameof(this.Subscriptions));

        }

        /// <summary>
        /// Views the subscription.
        /// </summary>
        /// <param name="subscriptionId">The subscription identifier.</param>
        /// <param name="planId">The plan identifier.</param>
        /// <param name="operation">The operation.</param>
        /// <returns> Subscriptions View. </returns>
        public IActionResult ViewSubscription(Guid subscriptionId, string planId, string operation)
        {
            if (!this.User.Identity.IsAuthenticated)
            {
                return this.RedirectToAction(nameof(this.Index));
            }

            var subscriptionDetail = new SubscriptionResultExtension();
            this.TempData["ShowWelcomeScreen"] = false;

            subscriptionDetail = this.subscriptionService.GetByCustomerEmail(_currentUserComponent.Email).FirstOrDefault();
            subscriptionDetail.ShowWelcomeScreen = false;
            subscriptionDetail.CustomerEmailAddress = _currentUserComponent.Email;
            subscriptionDetail.CustomerName = _currentUserComponent.Name;
            subscriptionDetail.SubscriptionParameters = this.subscriptionService.GetSubscriptionsParametersById(subscriptionId, subscriptionDetail.GuidPlanId);
            subscriptionDetail.IsAutomaticProvisioningSupported = Convert.ToBoolean(this.applicationConfigService.GetValueByName("IsAutomaticProvisioningSupported"));


            return this.View("Index", subscriptionDetail);
        }
    }
}
