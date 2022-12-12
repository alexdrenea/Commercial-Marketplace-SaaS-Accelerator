namespace Microsoft.Marketplace.Saas.Web.Controllers
{
    using System.Linq;
    using System.Threading.Tasks;
    using Microsoft.AspNetCore.Authentication;
    using Microsoft.AspNetCore.Authentication.OpenIdConnect;
    using Microsoft.AspNetCore.Mvc;
    using Microsoft.AspNetCore.Mvc.Filters;
    using Microsoft.Marketplace.SaaS.SDK.Services.Models;
    using Microsoft.Marketplace.SaaS.SDK.Services.Utilities;
    using Microsoft.Marketplace.SaasKit.Client.DataAccess.Services;

    /// <summary>
    /// Base Controller.
    /// </summary>
    /// <seealso cref="Microsoft.AspNetCore.Mvc.Controller" />
    public class BaseController : Controller
    {
        internal readonly CurrentUserComponent _currentUserComponent;

        /// <summary>
        /// Initializes a new instance of the <see cref="BaseController"/> class.
        /// </summary>
        public BaseController(CurrentUserComponent currentUserComponent)
        {
            _currentUserComponent = currentUserComponent;
            CheckAuthentication();
        }

        public override async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
        {
            await _currentUserComponent.SetCurrentUser(
                    email: HttpContext?.User?.Claims?.FirstOrDefault(s => s.Type == ClaimConstants.CLAIM_EMAILADDRESS)?.Value ?? string.Empty,
                    name: HttpContext?.User?.Claims?.FirstOrDefault(s => s.Type == ClaimConstants.CLAIM_NAME)?.Value ?? string.Empty
               );

            await base.OnActionExecutionAsync(context, next);
        }

        /// <summary>
        /// Checks the authentication.
        /// </summary>
        /// <returns>
        /// Check authentication.
        /// </returns>
        private IActionResult CheckAuthentication()
        {
            if (this.HttpContext == null || !this.HttpContext.User.Identity.IsAuthenticated)
            {
                return this.Challenge(new AuthenticationProperties { RedirectUri = "/" }, OpenIdConnectDefaults.AuthenticationScheme);
            }
            else
            {
                return this.RedirectToAction("Index", "Home", new { });
            }
        }
    }
}