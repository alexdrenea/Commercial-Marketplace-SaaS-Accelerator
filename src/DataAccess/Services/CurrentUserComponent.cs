using Microsoft.Marketplace.SaasKit.Client.DataAccess.Contracts;
using Microsoft.Marketplace.SaasKit.Client.DataAccess.Entities;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Mail;
using System.Text;
using System.Threading.Tasks;

namespace Microsoft.Marketplace.SaasKit.Client.DataAccess.Services
{
    public class CurrentUserComponent
    {
        private readonly IUsersRepository _usersRepository;

        public CurrentUserComponent(IUsersRepository userRepository)
        {
            _usersRepository = userRepository;
        }

        private Users User { get; set; }

        public bool IsAuthenticated { get { return User != null; } }

        public int UserId { get { return User?.UserId ?? -1; } }
        public string Name { get { return User?.FullName; } }
        public string Email { get { return User?.EmailAddress; } }


        public async Task SetCurrentUser(string email, string name)
        {
            if (string.IsNullOrEmpty(email) || string.IsNullOrEmpty(name))
                return;

            var result = _usersRepository.Save(new() {FullName = name, EmailAddress = email});
            if (result == -1)
                throw new Exception("Unable to save or retrieve current user. Please log-in");

            User = _usersRepository.GetPartnerDetailFromEmail(email);
        }
    }
}
