namespace Microsoft.Marketplace.SaasKit.Client.DataAccess.Services
{
    using System;
    using System.Collections.Generic;
    using System.ComponentModel.DataAnnotations;
    using System.Linq;
    using Microsoft.EntityFrameworkCore;
    using Microsoft.Marketplace.SaasKit.Client.DataAccess.Context;
    using Microsoft.Marketplace.SaasKit.Client.DataAccess.Contracts;
    using Microsoft.Marketplace.SaasKit.Client.DataAccess.Entities;

    /// <summary>
    /// Subscriptions Repository.
    /// </summary>
    /// <seealso cref="Microsoft.Marketplace.SaasKit.Client.DataAccess.Contracts.ISubscriptionsRepository" />
    public class SubscriptionsRepository : ISubscriptionsRepository
    {
        /// <summary>
        /// The context.
        /// </summary>
        private readonly SaasKitContext context;

        /// <summary>
        /// Scoped service that contains information about the loggedin user making the top line request for this call.
        /// </summary>
        private readonly ISubscriptionLogRepository _subscriptionLogRepository;

        /// <summary>
        /// The disposed.
        /// </summary>
        private bool disposed = false;

        /// <summary>
        /// Initializes a new instance of the <see cref="SubscriptionsRepository" /> class.
        /// </summary>
        /// <param name="context">The this.context.</param>
        public SubscriptionsRepository(SaasKitContext context, ISubscriptionLogRepository subscriptionLogRepository)
        {
            this.context = context;
            this._subscriptionLogRepository = subscriptionLogRepository;
        }

        /// <summary>
        /// Adds the specified subscription details.
        /// </summary>
        /// <param name="subscriptionDetails">The subscription details.</param>
        /// <returns> Subscription Detail Id.</returns>
        public int Save(Subscriptions subscriptionDetails)
        {
            var existingSubscriptions = this.context.Subscriptions.Where(s => s.AmpsubscriptionId == subscriptionDetails.AmpsubscriptionId).FirstOrDefault();
            if (existingSubscriptions != null)
            {
                var oldStatus = existingSubscriptions.SubscriptionStatus;
                var oldPlanId = existingSubscriptions.AmpplanId;
                var oldQuantity = existingSubscriptions.Ampquantity;
                existingSubscriptions.SubscriptionStatus = subscriptionDetails.SubscriptionStatus;
                existingSubscriptions.AmpplanId = subscriptionDetails.AmpplanId;
                existingSubscriptions.Ampquantity = subscriptionDetails.Ampquantity;
                this.context.Subscriptions.Update(existingSubscriptions);
                this.context.SaveChanges();

                if (oldStatus != existingSubscriptions.SubscriptionStatus)
                    _subscriptionLogRepository.AddAsync(subscriptionDetails.Id, "Status", existingSubscriptions.SubscriptionStatus, oldStatus).GetAwaiter().GetResult();
                if (oldPlanId != existingSubscriptions.AmpplanId)
                    _subscriptionLogRepository.AddAsync(subscriptionDetails.Id, "Plan", existingSubscriptions.AmpplanId, oldPlanId).GetAwaiter().GetResult();
                if (oldQuantity != existingSubscriptions.Ampquantity)
                    _subscriptionLogRepository.AddAsync(subscriptionDetails.Id, "Quantity", existingSubscriptions.Ampquantity.ToString(), oldQuantity.ToString()).GetAwaiter().GetResult();

                return existingSubscriptions.Id;
            }
            else
            {
                this.context.Subscriptions.Add(subscriptionDetails);
            }

            this.context.SaveChanges();
            return subscriptionDetails.Id;
        }

        /// <summary>
        /// Adds the specified subscription details.
        /// </summary>
        /// <param name="subscriptionId">The subscription identifier.</param>
        /// <param name="subscriptionStatus">The subscription status.</param>
        /// <param name="isActive">if set to <c>true</c> [is active].</param>
        public void UpdateStatusForSubscription(Guid subscriptionId, string subscriptionStatus, bool isActive)
        {
            var existingSubscription = this.context.Subscriptions.Where(s => s.AmpsubscriptionId == subscriptionId).FirstOrDefault();
            var oldStatus = existingSubscription?.SubscriptionStatus;
       
            if (existingSubscription == null || oldStatus == subscriptionStatus)
                return;

            existingSubscription.IsActive = isActive;
            existingSubscription.SubscriptionStatus = subscriptionStatus;

            context.Subscriptions.Update(existingSubscription);
            context.SaveChanges();

            _subscriptionLogRepository.AddAsync(existingSubscription.Id, "Status", existingSubscription.SubscriptionStatus, oldStatus).GetAwaiter().GetResult();
        }

        /// <summary>
        /// Updates the plan for subscription.
        /// </summary>
        /// <param name="subscriptionId">The subscription identifier.</param>
        /// <param name="planId">The plan identifier.</param>
        public void UpdatePlanForSubscription(Guid subscriptionId, string planId)
        {
            var existingSubscription = this.context.Subscriptions.Where(s => s.AmpsubscriptionId == subscriptionId).FirstOrDefault();
            var oldPlanId = existingSubscription?.AmpplanId;
            if (existingSubscription == null || oldPlanId == planId)
                return;

            existingSubscription.AmpplanId = planId;
            this.context.Subscriptions.Update(existingSubscription);
            this.context.SaveChanges();

            _subscriptionLogRepository.AddAsync(existingSubscription.Id, "Plan", existingSubscription.AmpplanId, oldPlanId).GetAwaiter().GetResult();
        }

        /// <summary>
        /// Updates the Quantity for subscription.
        /// </summary>
        /// <param name="subscriptionId">The subscription identifier.</param>
        /// <param name="quantity">The Quantity.</param>
        public void UpdateQuantityForSubscription(Guid subscriptionId, int quantity)
        {
            var existingSubscription = this.context.Subscriptions.Where(s => s.AmpsubscriptionId == subscriptionId).FirstOrDefault();
            var oldQuantity = existingSubscription?.Ampquantity;
            
            if (existingSubscription == null || oldQuantity == quantity)
                return;

            existingSubscription.Ampquantity = quantity;
            context.Subscriptions.Update(existingSubscription);
            context.SaveChanges();

            _subscriptionLogRepository.AddAsync(existingSubscription.Id, "Quantity", existingSubscription.Ampquantity.ToString(), oldQuantity.ToString()).GetAwaiter().GetResult();
        }

        /// <summary>
        /// Gets this instance.
        /// </summary>
        /// <returns> Subscriptions.</returns>
        public IEnumerable<Subscriptions> Get()
        {
            return this.context.Subscriptions.Include(s => s.User).OrderByDescending(s => s.CreateDate);
        }

        /// <summary>
        /// Gets the specified identifier.
        /// </summary>
        /// <param name="id">The identifier.</param>
        /// <returns> Subscriptions.</returns>
        public Subscriptions Get(int id)
        {
            return this.context.Subscriptions.Where(s => s.Id == id).FirstOrDefault();
        }

        /// <summary>
        /// Gets the subscriptions by email address.
        /// </summary>
        /// <param name="partnerEmailAddress">The partner email address.</param>
        /// <param name="subscriptionId">The subscription identifier.</param>
        /// <param name="isIncludeDeactvated">if set to <c>true</c> [is include deactvated].</param>
        /// <returns> List of Subscriptions.</returns>
        public IEnumerable<Subscriptions> GetSubscriptionsByEmailAddress(string partnerEmailAddress, bool isIncludeDeactvated = false)
        {
            return this.context.Subscriptions.Include(s => s.User).Where(s => s.User != null && s.User.EmailAddress == partnerEmailAddress);
        }

        /// <summary>
        /// Gets the subscriptions by ScheduleId.
        /// </summary>
        /// <param name="subscriptionId">The subscription identifier.</param>
        /// <param name="isIncludeDeactvated">if set to <c>true</c> [is include deactvated].</param>
        /// <returns> Subscriptions.</returns>
        public Subscriptions GetById(Guid subscriptionId, bool isIncludeDeactvated = false)
        {
            if (subscriptionId != default)
            {
                return this.context.Subscriptions.Include(s => s.User).Where(s => s.AmpsubscriptionId == subscriptionId).FirstOrDefault();
            }

            return null;
        }

        /// <summary>
        /// Gets the subscriptions by ScheduleId.
        /// </summary>
        /// <param name="subscriptionId">The subscription identifier.</param>
        /// <param name="planId">The plan identifier.</param>
        /// <returns>
        /// List of  Subscription Parameters.
        /// </returns>
        public List<SubscriptionParametersOutput> GetSubscriptionsParametersById(Guid subscriptionId, Guid planId)
        {
            if (subscriptionId != default)
            {
                var subscriptionParameters = this.context.SubscriptionParametersOutput.FromSqlRaw("dbo.spGetSubscriptionParameters {0},{1}", subscriptionId, planId).ToList();
                return subscriptionParameters.ToList();
            }

            return new List<SubscriptionParametersOutput>();
        }

        /// <summary>
        /// Updates the plan for subscription.
        /// </summary>
        /// <param name="subscriptionParametersOutput">The subscription parameters output.</param>
        public void AddSubscriptionParameters(SubscriptionParametersOutput subscriptionParametersOutput)
        {
            var existingSubscriptionparameter = this.context.SubscriptionAttributeValues.Where(s => s.Id == subscriptionParametersOutput.Id).FirstOrDefault();
            if (existingSubscriptionparameter != null)
            {
                existingSubscriptionparameter.OfferId = subscriptionParametersOutput.OfferId;
                this.context.SubscriptionAttributeValues.Update(existingSubscriptionparameter);
                this.context.SaveChanges();
            }
            else
            {
                SubscriptionAttributeValues newAttributeValue = new SubscriptionAttributeValues();
                newAttributeValue.OfferId = subscriptionParametersOutput.OfferId;
                newAttributeValue.PlanAttributeId = subscriptionParametersOutput.PlanAttributeId;
                newAttributeValue.Value = subscriptionParametersOutput.Value;
                newAttributeValue.SubscriptionId = subscriptionParametersOutput.SubscriptionId;
                newAttributeValue.CreateDate = subscriptionParametersOutput.CreateDate;
                newAttributeValue.UserId = subscriptionParametersOutput.UserId;
                newAttributeValue.PlanId = subscriptionParametersOutput.PlanId;
                this.context.SubscriptionAttributeValues.Add(newAttributeValue);
                this.context.SaveChanges();
            }
        }

        /// <summary>
        /// Removes the specified entity.
        /// </summary>
        /// <param name="entity">The entity.</param>
        public void Remove(Subscriptions entity)
        {
            this.context.Subscriptions.Remove(entity);
            this.context.SaveChanges();
        }

        /// <summary>
        /// Performs application-defined tasks associated with freeing, releasing, or resetting unmanaged resources.
        /// </summary>
        public void Dispose()
        {
            this.Dispose(true);

            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Releases unmanaged and - optionally - managed resources.
        /// </summary>
        /// <param name="disposing"><c>true</c> to release both managed and unmanaged resources; <c>false</c> to release only unmanaged resources.</param>
        protected virtual void Dispose(bool disposing)
        {
            if (!this.disposed)
            {
                if (disposing)
                {
                    this.context.Dispose();
                }
            }

            this.disposed = true;
        }
    }
}
