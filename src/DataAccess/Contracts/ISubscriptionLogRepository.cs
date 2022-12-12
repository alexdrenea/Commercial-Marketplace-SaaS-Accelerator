namespace Microsoft.Marketplace.SaasKit.Client.DataAccess.Contracts
{
    using System;
    using System.Collections.Generic;
    using System.Threading.Tasks;
    using Microsoft.Marketplace.SaasKit.Client.DataAccess.Entities;

    /// <summary>
    /// ISubscriptionLogRepository Interface.
    /// </summary>
    /// <seealso cref="Microsoft.Marketplace.SaasKit.Client.DataAccess.Contracts.IBaseRepository{Microsoft.Marketplace.SaasKit.Client.DataAccess.Entities.SubscriptionAuditLogs}" />
    /// <seealso cref="Microsoft.Marketplace.SaasKit.DataAccess.Contracts.IBaseRepository{Microsoft.Marketplace.SaasKit.DataAccess.Entities.SubscriptionAuditLogs}" />
    public interface ISubscriptionLogRepository : IBaseRepository<SubscriptionAuditLogs>
    {
        /// <summary>
        /// Adds a new Log entry to a subscription
        /// </summary>
        /// <param name="subscriptionId">Subscription Id</param>
        /// <param name="property">Property that changed</param>
        /// <param name="newValue">New value of the property</param>
        /// <param name="oldValue">Old Value of the property. N/A if not specified</param>
        /// <returns>Newly added Log object</returns>
        Task<SubscriptionAuditLogs> AddAsync(int subscriptionId, string property, string newValue, string oldValue = null);

        /// <summary>
        /// Gets the subscription by subscription identifier.
        /// </summary>
        /// <param name="subscriptionId">The subscription identifier.</param>
        /// <returns> Subscription Audit Logs.</returns>
        IEnumerable<SubscriptionAuditLogs> GetSubscriptionBySubscriptionId(Guid subscriptionId);

        /// <summary>
        /// Logs the status during provisioning.
        /// </summary>
        /// <param name="subscriptionID">The subscription identifier.</param>
        /// <param name="errorDescription">The error description.</param>
        /// <param name="subscriptionStatus">The subscription status.</param>
        void LogStatusDuringProvisioning(Guid subscriptionID, string errorDescription, string subscriptionStatus);
    }
}
