using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Marketplace.SaaS.SDK.Services.Utilities;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace Microsoft.Marketplace.SaaS.SDK.Services.Utilities
{
    public class ExceptionHandlerAttribute : ExceptionFilterAttribute
    {
        private readonly ILogger<ExceptionHandlerAttribute> _logger;
        private readonly IModelMetadataProvider _modelMetadataProvider;
        public ExceptionHandlerAttribute(IModelMetadataProvider modelMetadataProvider)
        {
            _logger = LoggerFactory.Create(builder => { builder.AddConsole(); }).CreateLogger<ExceptionHandlerAttribute>();
            _modelMetadataProvider = modelMetadataProvider;
        }

        public override void OnException(ExceptionContext context)
        {
            base.OnException(context);
            _logger.LogError(context.Exception, $"Exception: {context.Exception.Message} - {context.Exception.InnerException?.Message ?? ""}");

            var result = new ViewResult { ViewName = "Error" , };
            result.ViewData = new ViewDataDictionary(_modelMetadataProvider, context.ModelState);
            result.ViewData.Model = context.Exception;

            context.ExceptionHandled = true; // mark exception as handled
            // Returning response
            context.Result = result;
        }
    }
}
