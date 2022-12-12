// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See LICENSE file in the project root for license information.
namespace Microsoft.Marketplace.SaaS.SDK.Services.Services
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Text.Json;
    using global::Marketplace.SaaS.Accelerator.Services.Helpers;
    using Microsoft.Marketplace.SaaS.Models;
    using Microsoft.Marketplace.SaaS.SDK.Services.Models;
    using Microsoft.Marketplace.SaasKit.Client.DataAccess.Contracts;
    using Microsoft.Marketplace.SaasKit.Client.DataAccess.Entities;
    using Microsoft.Marketplace.SaasKit.Client.DataAccess.Services;

    /// <summary>
    /// Subscriptions Service.
    /// </summary>
    public class SubscriptionService
    {
        /// <summary>
        /// The subscription repository.
        /// </summary>
        private ISubscriptionsRepository subscriptionRepository;

        /// <summary>
        /// The plan repository.
        /// </summary>
        private IPlansRepository planRepository;

        private CurrentUserComponent currentUserComponent;

        /// <summary>
        /// Initializes a new instance of the <see cref="SubscriptionService"/> class.
        /// </summary>
        /// <param name="subscriptionRepo">The subscription repo.</param>
        /// <param name="planRepository">The plan repository.</param>
        /// <param name="currentUserId">The current user identifier.</param>
        public SubscriptionService(
            ISubscriptionsRepository subscriptionRepo, 
            CurrentUserComponent currentUserComponent,
            IPlansRepository planRepository)
        {
            this.subscriptionRepository = subscriptionRepo;
            this.planRepository = planRepository;
            this.currentUserComponent = currentUserComponent;
        }


        /// <summary>
        /// Adds/Update partner subscriptions.
        /// </summary>
        /// <param name="subscriptionDetail">The subscription detail.</param>
        /// <returns>Subscription Id.</returns>
        public int AddSubscription(SubscriptionResult subscriptionDetail, int customerUserId = 0)
        {
            if (subscriptionDetail == null) 
                return -1;

            var currentTime = DateTime.Now;
            var newSubscription = new Subscriptions()
            {
                Id = 0,
                AmpplanId = subscriptionDetail.PlanId,
                Ampquantity = subscriptionDetail.Quantity,
                AmpsubscriptionId = subscriptionDetail.Id,
                CreateBy = currentUserComponent.UserId,
                CreateDate = currentTime,
                IsActive = IsSubscriptionDeleted(subscriptionDetail.SaasSubscriptionStatus.ToString()),
                ModifyDate = currentTime,
                Name = subscriptionDetail.Name,
                SubscriptionStatus = Convert.ToString(subscriptionDetail.SaasSubscriptionStatus),
                UserId = customerUserId == 0 ? currentUserComponent.UserId : customerUserId,
                PurchaserEmail = subscriptionDetail.Purchaser.EmailId,
                PurchaserTenantId = subscriptionDetail.Purchaser.TenantId,
            };
            var subscriptionId = this.subscriptionRepository.Save(newSubscription);
            return subscriptionId;
        }


        public List<SubscriptionResultExtension> GetAll(bool includeUnsubscribed = true)
        {
            var allSubscriptions = this.subscriptionRepository.Get().ToList();
            //TODO: EXCEPTION -> PLAN ID might not be unique - need to use planGuid to cross reference, but we need OfferGuid as part of the Subscriptions table
            var allPlans = this.planRepository.Get().ToDictionary(p => p.PlanId);


            var res = allSubscriptions
                        .Select(sub => ToSubscriptionResult(sub, allPlans.GetOrDefault(sub.AmpplanId)))
                        .Where(sub => sub.SubscribeId > 0)
                        .ToList();
            return res;
        }

        /// <summary>
        /// Gets the subscriptions for partner.
        /// </summary>
        /// <param name="partnerEmailAddress">The partner email address.</param>
        /// <param name="subscriptionId">The subscription identifier.</param>
        /// <param name="includeUnsubscribed">if set to <c>true</c> [include unsubscribed].</param>
        /// <returns> subscription status.</returns>
        public List<SubscriptionResultExtension> GetByCustomerEmail(string partnerEmailAddress, bool includeUnsubscribed = true)
        {
            var allSubscriptionsForEmail = this.subscriptionRepository.GetSubscriptionsByEmailAddress(partnerEmailAddress, includeUnsubscribed).OrderByDescending(s => s.CreateDate).ToList();
            //TODO: EXCEPTION -> PLAN ID might not be unique - need to use planGuid to cross reference, but we need OfferGuid as part of the Subscriptions table
            var allPlans = this.planRepository.Get().ToDictionary(p => p.PlanId);

            var res = allSubscriptionsForEmail
                        .Select(sub => ToSubscriptionResult(sub, allPlans.GetOrDefault(sub.AmpplanId)))
                        .Where(sub => sub.SubscribeId > 0)
                        .ToList();

            return res;
        }
     
        /// <summary>
        /// Gets the subscriptions for subscription identifier.
        /// </summary>
        /// <param name="subscriptionId">The subscription identifier.</param>
        /// <param name="includeUnsubscribed">if set to <c>true</c> [include unsubscribed].</param>
        /// <returns> Subscription ResultExtension.</returns>
        public SubscriptionResultExtension GetBySubscriptionId(Guid subscriptionId, bool includeUnsubscribed = true)
        {
            return ToSubscriptionResult(subscriptionRepository.GetById(subscriptionId, includeUnsubscribed));
        }

        /// <summary>
        /// Get all Active subscription with Metered plan
        /// </summary>
        /// <returns>a list of subscription with metered plan</returns>
        public List<Subscriptions> GetActiveSubscriptionsWithMeteredPlan()
        {
            var allActiveSubscription = this.subscriptionRepository.Get().ToList().Where(s => s.SubscriptionStatus == "Subscribed").ToList();
            var allPlansData = this.planRepository.Get().ToList().Where(p => p.IsmeteringSupported == true).ToList();
            var meteredSubscriptions = from subscription in allActiveSubscription
                                       join plan in allPlansData
                                       on subscription.AmpplanId equals plan.PlanId
                                       select subscription;
            return meteredSubscriptions.ToList();
        }




        /// <summary>
        /// Binds the subscriptions.
        /// </summary>
        /// <param name="subscriptionId">The subscription identifier.</param>
        /// <param name="status">The status.</param>
        /// <param name="isActivate">if set to <c>true</c> [is activate].</param>
        public void UpdateSubscriptionStatus(Guid subscriptionId, string status, bool isActivate)
        {
            this.subscriptionRepository.UpdateStatusForSubscription(subscriptionId, status, isActivate);
        }

        /// <summary>
        /// Updates the subscription plan.
        /// </summary>
        /// <param name="subscriptionId">The subscription identifier.</param>
        /// <param name="planId">The plan identifier.</param>
        public void UpdateSubscriptionPlan(Guid subscriptionId, string planId)
        {
            if (subscriptionId != default && !string.IsNullOrWhiteSpace(planId))
            {
                this.subscriptionRepository.UpdatePlanForSubscription(subscriptionId, planId);
            }
        }

        /// <summary>
        /// Updates the subscription quantity.
        /// </summary>
        /// <param name="subscriptionId">The subscription identifier.</param>
        /// <param name="quantity">The quantity identifier.</param>
        public void UpdateSubscriptionQuantity(Guid subscriptionId, int quantity)
        {
            if (subscriptionId != default && quantity > 0)
            {
                this.subscriptionRepository.UpdateQuantityForSubscription(subscriptionId, quantity);
            }
        }


        /// <summary>
        /// Get the plan details for subscription.
        /// </summary>
        /// <returns> Plan Details.</returns>
        public List<PlanDetailResult> GetAllSubscriptionPlans()
        {
            var allPlans = this.planRepository.Get();

            return (from plan in allPlans
                    select new PlanDetailResult()
                    {
                        Id = plan.Id,
                        PlanId = plan.PlanId,
                        DisplayName = plan.DisplayName,
                        Description = plan.Description

                    }).ToList();
        }

        /// <summary>
        /// Get the plan details for subscription.
        /// </summary>
        /// <param name="subscriptionId">The subscription identifier.</param>
        /// <param name="planId">The plan identifier.</param>
        /// <returns>
        /// Subscription Parameters Model.
        /// </returns>
        public List<SubscriptionParametersModel> GetSubscriptionsParametersById(Guid subscriptionId, Guid planId)
        {
            List<SubscriptionParametersModel> subscriptionParametersList = new List<SubscriptionParametersModel>();

            var subscriptionParameters = this.subscriptionRepository.GetSubscriptionsParametersById(subscriptionId, planId);

            var serializedSubscription = JsonSerializer.Serialize(subscriptionParameters);
            subscriptionParametersList = JsonSerializer.Deserialize<List<SubscriptionParametersModel>>(serializedSubscription);

            return subscriptionParametersList;
        }

        /// <summary>
        /// Adds the plan details for subscription.
        /// </summary>
        /// <param name="subscriptionParameters">The subscription parameters.</param>
        /// <param name="currentUserId">The current user identifier.</param>
        public void AddSubscriptionParameters(UserModel customer, params SubscriptionParametersModel[] subscriptionParameters)
        {
            foreach (var parameters in subscriptionParameters)
            {
                this.subscriptionRepository.AddSubscriptionParameters(new SubscriptionParametersOutput
                {
                    Id = parameters.Id,
                    PlanId = parameters.PlanId,
                    DisplayName = parameters.DisplayName,
                    PlanAttributeId = parameters.PlanAttributeId,
                    SubscriptionId = parameters.SubscriptionId,
                    OfferId = parameters.OfferId,
                    Value = parameters.Value,
                    UserId = customer.UserId,
                    CreateDate = DateTime.Now,
                });
            }
        }

        /// <summary>
        /// Gets the subscription status.
        /// </summary>
        /// <param name="subscriptionStatus">The subscription status.</param>
        /// <returns> Subscription Status EnumExtension.</returns>
        private SubscriptionStatusEnumExtension GetSubscriptionStatus(string subscriptionStatus)
        {
            var parseSuccessfull = Enum.TryParse(subscriptionStatus, out SubscriptionStatusEnumExtension status);
            return parseSuccessfull ? status : SubscriptionStatusEnumExtension.UnRecognized;
        }


        /// <summary>
        /// Prepares the subscription response.
        /// </summary>
        /// <param name="subscription">The subscription.</param>
        /// <returns> Subscription.</returns>
        private SubscriptionResultExtension ToSubscriptionResult(Subscriptions subscription, Plans existingPlanDetail = null)
        {
            if (subscription == null) return new SubscriptionResultExtension();

            existingPlanDetail ??= this.planRepository.GetById(subscription.AmpplanId);

            var subscritpionDetail = new SubscriptionResultExtension
            {
                Id = subscription.AmpsubscriptionId,
                SubscribeId = subscription.Id,
                PlanId = subscription.AmpplanId ?? string.Empty,
                Quantity = subscription.Ampquantity,
                Name = subscription.Name,
                SubscriptionStatus = this.GetSubscriptionStatus(subscription.SubscriptionStatus),
                IsActiveSubscription = subscription.IsActive ?? false,
                CustomerEmailAddress = subscription.User?.EmailAddress,
                CustomerName = subscription.User?.FullName,
                IsMeteringSupported = existingPlanDetail?.IsmeteringSupported ?? false,
                GuidPlanId = existingPlanDetail.PlanGuid,
                IsPerUserPlan = subscription.Ampquantity > 0,
                Purchaser = new PurchaserResult
                {
                    EmailId = subscription.PurchaserEmail,
                    TenantId = subscription.PurchaserTenantId ?? default
                }
            };

            return subscritpionDetail;
        }


        /// <summary>
        /// Subscriptions state from status.
        /// </summary>
        /// <param name="status">The status.</param>
        /// <returns> check if subscription deleted.</returns>
        private bool IsSubscriptionDeleted(string status)
        {
            return SaaS.Models.SubscriptionStatusEnum.Unsubscribed.ToString().Equals(status, StringComparison.InvariantCultureIgnoreCase);
        }

    }
}