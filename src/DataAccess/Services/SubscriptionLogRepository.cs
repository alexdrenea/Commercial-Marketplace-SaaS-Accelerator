namespace Microsoft.Marketplace.SaasKit.Client.DataAccess.Services
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading.Tasks;
    using Microsoft.EntityFrameworkCore;
    using Microsoft.Marketplace.SaasKit.Client.DataAccess.Context;
    using Microsoft.Marketplace.SaasKit.Client.DataAccess.Contracts;
    using Microsoft.Marketplace.SaasKit.Client.DataAccess.Entities;

    /// <summary>
    /// Subscription Log Repository.
    /// </summary>
    /// <seealso cref="Microsoft.Marketplace.SaasKit.Client.DataAccess.Contracts.ISubscriptionLogRepository" />
    public class SubscriptionLogRepository : ISubscriptionLogRepository
    {
        /// <summary>
        /// The context.
        /// </summary>
        private readonly SaasKitContext context;

        /// <summary>
        /// Scoped service that contains information about the loggedin user making the top line request for this call.
        /// </summary>
        private readonly CurrentUserComponent _currentUserComponent;
       

        /// <summary>
        /// Initializes a new instance of the <see cref="SubscriptionLogRepository"/> class.
        /// </summary>
        /// <param name="context">The this.context.</param>
        public SubscriptionLogRepository(SaasKitContext context, CurrentUserComponent currentUserComponent)
        {
            this.context = context;
            _currentUserComponent = currentUserComponent;   
        }

        /// <summary>
        /// Adds the specified subscription logs.
        /// </summary>
        /// <param name="subscriptionLogs">The subscription logs.</param>
        /// <returns> log Id.</returns>
        public int Save(SubscriptionAuditLogs subscriptionLogs)
        {
            this.context.SubscriptionAuditLogs.Add(subscriptionLogs);
            this.context.SaveChanges();
            return subscriptionLogs.Id;
        }

        public async Task<SubscriptionAuditLogs> AddAsync(int subscriptionId, string property, string newValue, string oldValue = null)
        {
            var newLog = new SubscriptionAuditLogs()
            {
                Attribute = property,
                SubscriptionId = subscriptionId,
                NewValue = newValue,
                OldValue = oldValue ?? "N/A",
                CreateBy = _currentUserComponent.UserId,
                CreateDate = DateTime.Now,
            };
            context.SubscriptionAuditLogs.Add(newLog);
            await context.SaveChangesAsync();

            return newLog;
        }


        /// <summary>
        /// Gets this instance.
        /// </summary>
        /// <returns> List of log.</returns>
        public IEnumerable<SubscriptionAuditLogs> Get()
        {
            return this.context.SubscriptionAuditLogs.Include(s => s.Subscription);
        }

        /// <summary>
        /// Gets the subscription by subscription identifier.
        /// </summary>
        /// <param name="subscriptionId">The subscription identifier.</param>
        /// <returns> Subscription Audit Logs.</returns>
        public IEnumerable<SubscriptionAuditLogs> GetSubscriptionBySubscriptionId(Guid subscriptionId)
        {
            return this.context.SubscriptionAuditLogs.Include(s => s.Subscription).Where(s => s.Subscription.AmpsubscriptionId == subscriptionId);
        }

        /// <summary>
        /// Gets the specified identifier.
        /// </summary>
        /// <param name="id">The identifier.</param>
        /// <returns> Subscription Audit Logs.</returns>
        public SubscriptionAuditLogs Get(int id)
        {
            return this.context.SubscriptionAuditLogs.Where(s => s.Id == id).FirstOrDefault();
        }

        /// <summary>
        /// Removes the specified entity.
        /// </summary>
        /// <param name="entity">The entity.</param>
        public void Remove(SubscriptionAuditLogs entity)
        {
            this.context.SubscriptionAuditLogs.Remove(entity);
            this.context.SaveChanges();
        }

        /// <summary>
        /// Logs the status during provisioning.
        /// </summary>
        /// <param name="subscriptionID">The subscription identifier.</param>
        /// <param name="errorDescription">The error description.</param>
        /// <param name="subscriptionStatus">The subscription status.</param>
        public void LogStatusDuringProvisioning(Guid subscriptionID, string errorDescription, string subscriptionStatus)
        {
            var subscription = this.context.Subscriptions.Where(s => s.AmpsubscriptionId == subscriptionID).FirstOrDefault();

            WebJobSubscriptionStatus status = new WebJobSubscriptionStatus()
            {
                SubscriptionId = subscriptionID,
                SubscriptionStatus = subscriptionStatus,
                Description = errorDescription,
                InsertDate = DateTime.Now,
            };
            this.context.WebJobSubscriptionStatus.Add(status);
            this.context.SaveChanges();
        }
    }
}
