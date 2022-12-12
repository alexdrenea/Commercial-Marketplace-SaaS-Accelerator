namespace Microsoft.Marketplace.SaaS.SDK.Services.Services
{
    using System;
    using Microsoft.Marketplace.SaaS.SDK.Services.Models;
    using Microsoft.Marketplace.SaasKit.Client.DataAccess.Contracts;
    using Microsoft.Marketplace.SaasKit.Client.DataAccess.Entities;

    /// <summary>
    /// Users Service.
    /// </summary>
    public class UserService
    {
        /// <summary>
        /// The user repository.
        /// </summary>
        private IUsersRepository userRepository;

        /// <summary>
        /// Initializes a new instance of the <see cref="UserService" /> class.
        /// </summary>
        /// <param name="userRepository">The user repository.</param>
        public UserService(IUsersRepository userRepository)
        {
            this.userRepository = userRepository;
        }

        /// <summary>
        /// Adds the partner detail.
        /// </summary>
        /// <param name="userModel">The partner detail view model.</param>
        /// <returns> User id.</returns>
        public UserModel AddUser(UserModel userModel)
        {
            if (!string.IsNullOrEmpty(userModel.EmailAddress))
            {
                Users newPartnerDetail = new Users()
                {
                    UserId = userModel.UserId,
                    EmailAddress = userModel.EmailAddress,
                    FullName = userModel.FullName,
                    CreatedDate = DateTime.Now,
                };
                var userId = this.userRepository.Save(newPartnerDetail);
                userModel.UserId = userId;
                return userModel;
            }

            return null;
        }

        /// <summary>
        /// Gets the user identifier from email address.
        /// </summary>
        /// <param name="partnerEmail">The partner email.</param>
        /// <returns>returns user id.</returns>
        public UserModel GetUserFromEmailAddress(string partnerEmail)
        {
            if (string.IsNullOrEmpty(partnerEmail))
                return null;

            var user = this.userRepository.GetPartnerDetailFromEmail(partnerEmail);
            if (user == null) 
                    return null;

            return new UserModel
            {
                UserId = user.UserId,
                EmailAddress = user.EmailAddress,
                FullName = user.FullName,
                CreatedDate = user.CreatedDate
            };
        }
    }
}